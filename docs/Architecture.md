# Architecture

This document describes the internal architecture of Hina, including the project structure, core library design, data flow pipelines, key classes, and design decisions.

---

## Project Structure

Hina is organized into four projects plus a test project:

| Project | Type | Description |
|---------|------|-------------|
| **Hina.Core** | Class Library | Core engine: patching, rsync matching, manifest handling, chunking, hashing, signing, compression, networking, configuration |
| **Hina.CLI** | Console App | Command-line client patcher that wraps Hina.Core |
| **Hina.Builder** | Console App | Manifest generator and chunk store builder |
| **Hina.Host** | ASP.NET Core App | Lightweight static file server for serving patches |
| **Hina.Core.Tests** | xUnit Test Project | Unit and integration tests for the core library |

### Dependency Graph

```
Hina.CLI ---------> Hina.Core
Hina.Builder -----> Hina.Core
Hina.Host           (standalone, serves static files)
Hina.Core.Tests --> Hina.Core
```

Hina.CLI and Hina.Builder both depend on Hina.Core. Hina.Host is a standalone ASP.NET Core application that serves the build output as static files and has no dependency on Hina.Core.

---

## Core Library Internals

The `Hina.Core` library is organized into the following namespaces, each in its own directory:

### Patching/

The primary API surface for clients.

| Class | Purpose |
|-------|---------|
| `IPatchClient` | Public interface defining `CheckAsync`, `PatchAsync`, `VerifyAsync`, `RollbackAsync` |
| `PatchClient` | Default implementation using rsync-like rolling checksum matching |
| `PatchResults` | Result types: `CheckResult`, `PatchResult`, `VerifyResult` |
| `PatchJournal` | Tracks backup entries across a patch session for crash-safe rollback |
| `PatchCleanup` | Removes leftover `.hina.tmp` and `.hina.bak` files and the journal |

### Rsync/

Rolling checksum and chunking algorithms.

| Class | Purpose |
|-------|---------|
| `IChunker` | Interface for chunking strategies; returns `List<ManifestChunk>` from a stream |
| `RollingChecksum` | Static Adler32-variant rolling checksum (mod 65521) with `Compute` and `Roll` |
| `RsyncChunker` | Fixed-size chunker that produces chunks with weak (rolling) and strong (SHA-256) checksums |
| `ContentDefinedChunker` | Variable-size chunker using a Gear hash to find content-defined boundaries |

### Manifest/

Manifest data model and serialization.

| Class | Purpose |
|-------|---------|
| `Manifest` | Root object: Version, BuildId, BaseUrl, Files list, optional Signature |
| `ManifestFile` | Per-file entry: Path, Size, MTimeUtc, FileHash, ChunkSize, Chunks list |
| `ManifestChunk` | Per-chunk entry: Index, Weak (uint), Strong (string), Size |
| `ManifestBuilder` | Scans a directory and produces a Manifest using a given IChunker |
| `ManifestSerializer` | JSON serialization/deserialization for Manifest objects |
| `ManifestSignature` | Signature metadata: Algorithm, Signature, PublicKey |
| `ManifestSigner` | Signs and verifies manifests with Ed25519 via NSec |

### Net/

HTTP client and retry logic.

| Class | Purpose |
|-------|---------|
| `HttpChunkClient` | Fetches manifests and chunks over HTTP; resolves chunk URLs using hash bucketing |
| `RetryPolicy` | Exponential backoff with jitter for transient HTTP 5xx and network errors |

### Chunking/

Chunk store generation for the builder.

| Class | Purpose |
|-------|---------|
| `ChunkStoreWriter` | Writes Brotli-compressed chunk files to disk, organized by hash-prefix buckets |

### Compression/

| Class | Purpose |
|-------|---------|
| `BrotliCodec` | Static `Compress` and `Decompress` methods using .NET built-in BrotliStream |

### Hashing/

| Class | Purpose |
|-------|---------|
| `IHasher` | Interface with `AlgorithmId` property and `ComputeHashAsync` method |
| `Sha256Hasher` | SHA-256 implementation returning `sha256:<hex>` formatted hashes |
| `Hex` | Internal helper for byte-to-hex conversion |

### Crypto/

| Class | Purpose |
|-------|---------|
| `KeyGenerator` | Generates Ed25519 key pairs using NSec, returns Base64-encoded keys |

### Configuration/

| Class | Purpose |
|-------|---------|
| `PatcherConfig` | Immutable config object with all patcher settings (init-only properties) |
| `PatcherConfigLoader` | Loads PatcherConfig from a JSON file using System.Text.Json |

### IO/

| Class | Purpose |
|-------|---------|
| `PathUtils` | Internal helper for normalizing manifest paths and converting to OS paths |

---

## Data Flow

### Build Pipeline

The builder (`Hina.Builder`) takes a directory of application files and produces a manifest and chunk store.

```
 Input directory (game/app files)
         |
         v
 [1] Enumerate all files recursively
         |
         v
 [2] For each file:
     +-- Open FileStream
     +-- Chunk the file (IChunker: RsyncChunker or ContentDefinedChunker)
     |   +-- Compute rolling checksum (weak) for each chunk
     |   +-- Compute SHA-256 hash (strong) for each chunk
     +-- Compute full-file SHA-256 hash
     +-- Produce ManifestFile with chunk list
         |
         v
 [3] Assemble Manifest object
     +-- Set Version, BaseUrl, BuildId
     +-- Attach ManifestFile entries
         |
         v
 [4] (Optional) Sign manifest with Ed25519 private key
     +-- Serialize manifest without signature to canonical JSON
     +-- Sign canonical bytes with NSec Ed25519
     +-- Attach ManifestSignature (algorithm, signature, public key)
         |
         v
 [5] Write manifest.json to output directory
         |
         v
 [6] Write chunks to output/chunks/ directory
     +-- For each chunk, Brotli-compress the raw bytes
     +-- Store as chunks/<bucket>/<hash>.chunk.br
     +-- Bucket = first 2 hex characters of hash
     +-- Deduplicate: skip if file already exists
         |
         v
 Output: manifest.json + chunks/ directory
```

### Client Patch Pipeline

The client (`PatchClient`) downloads the manifest, matches local data, and applies changes.

```
 [1] Fetch manifest.json from server
     +-- URL: <BaseUrl>/manifest.json (or manifest.<channel>.json)
     +-- Retry with exponential backoff on transient errors
         |
         v
 [2] Verify Ed25519 signature (if TrustedPublicKey is configured)
     +-- Serialize manifest without signature to canonical JSON
     +-- Verify signature against trusted public key
     +-- Reject patch if verification fails
         |
         v
 [3] Check for incomplete previous patch
     +-- Load journal from .hina/journal.json
     +-- If found, rollback previous patch first
         |
         v
 [4] Create new PatchJournal
         |
         v
 [5] For each file in manifest:
     +-- Compute local file hash
     +-- Skip if hash matches (already up to date)
     +-- Build weak checksum lookup table from manifest chunks
     +-- Rsync match: slide rolling checksum window over local file
     |   +-- On weak match, compute strong hash to confirm
     |   +-- Record matched chunks with their local file offsets
     +-- Rebuild file to temp path (.hina.tmp):
     |   +-- For matched chunks: copy bytes from local file
     |   +-- For missing chunks: download from server
     |       +-- URL: <BaseUrl>/chunks/<bucket>/<hash>.chunk.br
     |       +-- Decompress Brotli, write to output
     +-- Verify rebuilt file hash against manifest
     +-- Backup original file (.hina.bak) if Backup is enabled
     +-- Record backup in journal
     +-- Swap temp file into place
         |
         v
 [6] On success: mark journal as "Completed"
     On failure: rollback all files from backups, mark journal as "Failed"
```

### Rollback Flow

```
 [1] Load journal from .hina/journal.json
 [2] For each journal entry:
     +-- Copy .hina.bak back to original path
     +-- Delete .hina.bak
 [3] Delete journal file
```

### Cleanup Flow

```
 [1] Recursively scan target directory
 [2] Delete all *.hina.tmp files
 [3] Delete all *.hina.bak files
 [4] Delete .hina/journal.json
```

---

## Class Diagram

```
+---------------------+        +----------------------+
|    IPatchClient      |        |     IChunker         |
|---------------------|        |----------------------|
| + Config            |        | + ChunkAsync()       |
| + CheckAsync()      |        +----------+-----------+
| + PatchAsync()      |                   |
| + VerifyAsync()     |          +--------+--------+
| + RollbackAsync()   |          |                 |
+----------+----------+   +------+------+   +------+-------+
           |              | RsyncChunker|   | ContentDefined|
           |              |             |   | Chunker       |
+----------+----------+  +------+------+   +------+--------+
|    PatchClient       |         |                 |
|---------------------|         |                 |
| - _hasher: IHasher  |         v                 v
| - _http             |  +------+------+   +------+--------+
| - _logger           |  | Rolling     |   | Gear hash     |
| + Config            |  | Checksum    |   | boundary      |
| + CheckAsync()      |  | (Adler32)   |   | detection     |
| + PatchAsync()      |  +-------------+   +---------------+
| + VerifyAsync()     |
| + RollbackAsync()   |
| - RsyncMatchLocal() |
| - VerifyManifest()  |
+---------+-----------+
          |
          | uses
          v
+---------+-----------+    +-------------------+
|  HttpChunkClient    |--->|   RetryPolicy     |
|---------------------|    |-------------------|
| + GetManifestAsync()|    | - _maxRetries     |
| + GetChunkAsync()   |    | - _baseDelayMs    |
+---------------------+    | + ExecuteAsync()  |
                           | + CalculateDelay()|
                           | + IsTransient()   |
                           +-------------------+

+---------+-----------+    +-------------------+
|     IHasher         |    |  ManifestSigner   |
|---------------------|    |-------------------|
| + AlgorithmId       |    | + AttachSignature |
| + ComputeHashAsync()|    | + Verify()        |
+----------+----------+    +-------------------+
           |
+----------+----------+    +-------------------+
|   Sha256Hasher       |    |  BrotliCodec      |
|---------------------|    |-------------------|
| + AlgorithmId="sha256"|  | + Compress()      |
| + ComputeHashAsync()|    | + Decompress()    |
+---------------------+    +-------------------+

+---------------------+    +-------------------+
|  ManifestBuilder     |    | ChunkStoreWriter  |
|---------------------|    |-------------------|
| - _hasher           |    | - _hasher         |
| + BuildAsync()      |    | - _chunkSize      |
+---------------------+    | - _chunker        |
                           | + WriteChunksAsync |
+---------------------+    +-------------------+
|   PatchJournal       |
|---------------------|    +-------------------+
| + Status            |    |  PatcherConfig    |
| + CreatedUtc        |    |-------------------|
| + Entries           |    | + BaseUrl         |
| + Load()            |    | + Channel         |
| + SaveAsync()       |    | + Concurrency     |
+---------------------+    | + ChunkSize       |
                           | + Verify, Backup  |
+---------------------+    | + TrustedPublicKey|
|   Manifest           |    | + MaxRetries      |
|---------------------|    | + RetryBaseDelayMs|
| + Version           |    | + ChunkingMode    |
| + BuildId           |    | + MinChunkSize    |
| + BaseUrl           |    | + MaxChunkSize    |
| + Files             |    | + AvgChunkSize    |
| + Signature         |    +-------------------+
+---------------------+
          |
          | contains
          v
+---------------------+
|   ManifestFile       |
|---------------------|
| + Path              |
| + Size              |
| + MTimeUtc          |
| + FileHash          |
| + ChunkSize         |
| + Chunks            |
+----------+----------+
           |
           | contains
           v
+----------+----------+
|   ManifestChunk      |
|---------------------|
| + Index             |
| + Weak (uint)       |
| + Strong (string)   |
| + Size              |
+---------------------+
```

---

## Key Interfaces

### IPatchClient

The primary public API for embedding the patcher in your application.

```csharp
public interface IPatchClient
{
    PatcherConfig Config { get; }
    Task<CheckResult> CheckAsync(string rootDir, CancellationToken ct);
    Task<PatchResult> PatchAsync(string rootDir, CancellationToken ct);
    Task<VerifyResult> VerifyAsync(string rootDir, CancellationToken ct);
    Task RollbackAsync(string rootDir, CancellationToken ct);
}
```

- `CheckAsync` -- compares local file hashes to the manifest without downloading. Returns whether updates are available.
- `PatchAsync` -- downloads and applies all missing or changed files. Returns list of applied files and success status.
- `VerifyAsync` -- verifies integrity of all local files against manifest hashes. Returns list of broken files.
- `RollbackAsync` -- restores files from backups using the patch journal.

### IChunker

Abstraction over chunking strategies.

```csharp
public interface IChunker
{
    Task<List<ManifestChunk>> ChunkAsync(Stream stream, CancellationToken ct);
}
```

Two implementations:

- `RsyncChunker` -- fixed-size chunks (default). Reads the stream in blocks of `chunkSize` bytes.
- `ContentDefinedChunker` -- variable-size chunks using Gear hash boundary detection. Chunk sizes range between `minSize` and `maxSize`, targeting `avgSize`.

### IHasher

Abstraction over hash algorithms.

```csharp
public interface IHasher
{
    string AlgorithmId { get; }
    Task<string> ComputeHashAsync(Stream stream, CancellationToken ct);
}
```

Single implementation: `Sha256Hasher` returning hashes in `sha256:<hex>` format. The `AlgorithmId` prefix allows future algorithm changes without ambiguity.

---

## Design Decisions

### Why rsync rolling checksum?

The rsync algorithm allows the patcher to identify blocks in a local file that match remote chunks, even when the file has been partially modified. By sliding a rolling checksum window across the local file and comparing weak checksums against the manifest, the client can reuse existing data and only download the chunks that differ. This dramatically reduces bandwidth for incremental updates.

The rolling checksum uses an Adler32 variant (mod 65521) that can be updated in O(1) as the window slides by one byte. Weak matches are confirmed with a strong SHA-256 hash to eliminate false positives.

### Why Brotli compression?

Brotli provides superior compression ratios compared to gzip and deflate, particularly for binary data common in game assets. It is natively supported in .NET via `BrotliStream` with no external dependencies. The `CompressionLevel.Optimal` setting balances compression ratio against build time.

### Why Ed25519 signing?

Ed25519 provides strong cryptographic guarantees with small key sizes (32 bytes) and fast verification. The NSec library is a well-audited .NET binding for libsodium. Manifest signing ensures that clients only apply updates from a trusted source, preventing man-in-the-middle attacks on the update channel.

The signing process serializes the manifest without the signature field to produce canonical bytes, signs those bytes, and attaches the signature. Verification repeats the canonical serialization and checks the signature against a trusted public key distributed with the client.

### Why hash bucketing for chunk storage?

Chunk files are stored in subdirectories named by the first two hex characters of the content hash (e.g., `chunks/a3/a3f7...chunk.br`). This limits any single directory to approximately 256 subdirectories, each containing a manageable number of files. Without bucketing, a large game with millions of chunks would produce a single directory with millions of entries, degrading file system performance on all major operating systems.

### Why content-defined chunking (CDC)?

Fixed-size chunking works well when files change in-place, but insertions or deletions shift all subsequent chunk boundaries, invalidating every downstream chunk. CDC uses a Gear hash to find natural boundaries based on file content. When bytes are inserted or deleted, only the chunks near the change are affected, and chunks elsewhere in the file retain their original boundaries and hashes. This provides significantly better deduplication for files that frequently grow, shrink, or have data inserted.

The Gear hash implementation uses a precomputed 256-entry lookup table with a deterministic xorshift64 PRNG. A bitmask with `log2(avgSize)` low bits controls the average chunk size. Scanning begins after `minSize` bytes to enforce the minimum, and a hard cutoff at `maxSize` ensures chunks never exceed the maximum.
