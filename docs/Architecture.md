# Architecture

This document describes the internal architecture of Hina, including the project structure, core library design, data flow pipelines, key classes, and design decisions.

---

## Project Structure

Hina is organized into five projects plus two test projects:

| Project | Type | Description |
|---------|------|-------------|
| **Hina.Core** | Class Library | Core engine: patching, rsync matching, manifest handling, chunking, hashing, signing, compression, networking, configuration |
| **Hina.PackageManager** | Class Library | Package-manager layer: descriptor schema, validator, signer/fetcher, install/uninstall/update/reinstall services, hook executor, per-OS shell integration, local registry |
| **Hina.CLI** | Console App (NativeAOT) | End-user CLI (`hina install/update/uninstall/list/info/which/reinstall`) plus developer subcommands under `hina dev <cmd>` |
| **Hina.Builder** | Console App | Manifest generator and chunk store builder |
| **Hina.Host** | ASP.NET Core App | Lightweight static file server for serving patches |
| **Hina.Core.Tests** | xUnit Test Project | Unit and integration tests for the core engine |
| **Hina.PackageManager.Tests** | xUnit Test Project | Unit + cross-platform integration tests for the package-manager layer |

### Dependency Graph

```
Hina.CLI ------------------> Hina.PackageManager ----> Hina.Core
Hina.CLI ------------------> Hina.Core                 (also direct, for `hina dev`)
Hina.Builder --------------> Hina.Core
Hina.Host                     (standalone, serves static files)
Hina.Core.Tests -----------> Hina.Core
Hina.PackageManager.Tests -> Hina.PackageManager + Hina.Core
```

`Hina.CLI` references both `Hina.PackageManager` (for the top-level package commands)
and `Hina.Core` directly (for the patcher subcommands surfaced under `hina dev`).
`Hina.PackageManager` reuses `Hina.Core.PatchClient` for delta chunk downloads — no
parallel engine. `Hina.Host` remains a standalone ASP.NET Core application.

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
| `PatcherConfig` | Config object with all patcher settings (mutable `set` properties so source-generated JSON deserialization runs default-initializers correctly under NativeAOT) |
| `PatcherConfigLoader` | Loads PatcherConfig from a JSON file using `System.Text.Json` source-generation |

### IO/

| Class | Purpose |
|-------|---------|
| `PathUtils` | Internal helper for normalizing manifest paths and converting to OS paths |

### Json/

| Class | Purpose |
|-------|---------|
| `HinaCoreJsonContext` | Source-generated JSON metadata for `Manifest`, `PatcherConfig`, `PatchJournal`. Default-mode context used for reads (case-insensitive). |
| `HinaCoreIndentedJsonContext` | Source-gen context with `WriteIndented = true` for human-readable writes. |
| `HinaCoreCanonicalJsonContext` | Source-gen context for `ManifestSigner`'s canonical bytes (compact, null-ignoring) — keeps signatures byte-deterministic. |

---

## Hina.PackageManager Library Internals

The `Hina.PackageManager` library layers a package-manager surface on top of `Hina.Core`. It does not implement its own patching engine — `InstallService` and `UpdateService` reuse `Hina.Core.PatchClient` directly.

### Descriptor/

The `hina.app.json` wire format authored by publishers.

| Class | Purpose |
|-------|---------|
| `AppDescriptor` | Root type: name, version, publisher, baseUrl, channel, publicKey, exec map, entries, postInstall hooks, descriptorSignature |
| `ExecMap` | Per-OS path to the app's main executable (`windows`, `linux`, `macos`) |
| `ShellEntry` | A single shell-visible entry (Start Menu item / `.desktop` / macOS bundle alias) |
| `HookAction` (+ subtypes) | Discriminated polymorphic hook with `AddToPathHook`, `MimeTypeHook`, `UrlSchemeHook`, `InstallFontHook`, `AutostartHook` |
| `DescriptorSignature` | Ed25519 signature carrier on the descriptor itself (parallel to `ManifestSignature` on the manifest) |
| `DescriptorParser` | Parse / serialize via source-gen JSON; produces canonical bytes for signing |
| `DescriptorValidator` | Validates schema invariants (name regex, SemVer, HTTPS, path traversal, entry-id references) |
| `DescriptorSigner` | Ed25519 sign / verify over canonical bytes (NSec, same algorithm as `ManifestSigner`) |
| `DescriptorFetcher` | HTTP fetcher with size cap and read-stream limiter |

### Registry/

The local index of installed apps.

| Class | Purpose |
|-------|---------|
| `Registry` | Root JSON object: `apps: Dictionary<name, InstalledApp>` |
| `InstalledApp` | Per-app row: pinned baseUrl/publicKey/channel, install path, descriptorUrl, executed hooks, shell entries |
| `HookEvidence` | What was actually written on disk by a hook — read at uninstall time |
| `ShellEntryRecord` | Id-keyed pair so update-flow diff can replace renamed/removed entries |
| `RegistryStore` | Atomic read/write (tmp + fsync + rename); uses source-gen JSON |
| `LockManager` | `FileShare.None` exclusive lock on `registry.json.lock` with exponential-backoff retry |

### Install/

End-to-end orchestration services.

| Class | Purpose |
|-------|---------|
| `InstallService` | `hina install <url>` flow: fetch → validate → verify signature → TOFU prompt → PatchClient.PatchAsync → shell entries → hooks → registry write |
| `UninstallService` | `hina uninstall <name>` flow: replay hook evidence in reverse, remove shell entries, delete app dir + descriptor cache, update registry |
| `UpdateService` | `hina update [name]` flow: re-fetch descriptor, verify against pinned key, diff hooks/entries by identity, delta-patch, apply diff |
| `ReinstallService` | `hina reinstall <name>` flow: fetch + key-rotation check **before** uninstall, then uninstall + install |
| `InstallTransaction` | Journals each side-effect so a mid-flight exception unwinds in reverse |
| `InstallOptions` / `UpdateOptions` / `InstallResult` / `UpdateResult` / `UninstallResult` / `TrustPrompt` | Plain data shapes for service inputs / outputs |

### Hooks/

| Class | Purpose |
|-------|---------|
| `HookExecutor` | Dispatches `HookAction` to the active `IPlatformIntegration`; returns `HookEvidence` |
| `HookIdentity` | Stable string identity per hook (action + key fields) — drives the update-flow diff |

### Platform/

Per-OS shell integration. Pure interface + factory + three implementations.

| Class | Purpose |
|-------|---------|
| `IPlatformIntegration` | All shell-touching operations: shortcuts, AddToPath, MIME, URL scheme, font, autostart (+ `Remove`/`Unregister` counterparts). Returns "evidence" strings stored in the registry. |
| `PlatformIntegrationFactory` | Picks the right impl via `RuntimeInformation.IsOSPlatform` |
| `LinuxPlatformIntegration` | `.desktop` files in `~/.local/share/applications`, symlinks in `~/.local/bin`, fonts in `~/.local/share/fonts`, `~/.config/autostart/*.desktop` |
| `WindowsPlatformIntegration` | `.lnk` shortcuts via COM `IShellLink`, `.cmd` shims in `%LOCALAPPDATA%\Hina\bin` (PATH-extended), HKCU registry for MIME/URL/autostart, per-user fonts |
| `MacOSPlatformIntegration` | Minimal `.app` bundles in `~/Applications` with generated Info.plist, helper bundles with `CFBundleDocumentTypes` / `CFBundleURLTypes`, `~/Library/Fonts`, `~/Library/LaunchAgents/*.plist` |
| `Windows/ShellLink.cs` | Hand-rolled COM interop (`IShellLinkW` + `IPersistFile`) using the `[CoClass]` idiom; AOT-compatible |

### Paths/

| Class | Purpose |
|-------|---------|
| `InstallPaths` | Per-OS roots (apps dir, registry file, descriptor cache, user bin dir). `ForCurrentOs()` for production, `ForRoot(temp)` for tests |

### Json/

| Class | Purpose |
|-------|---------|
| `PackageManagerJsonContext` | Source-gen JSON for `AppDescriptor`, polymorphic `HookAction` subtypes, `Registry`. Case-insensitive read + camelCase write naming policy. |
| `PackageManagerIndentedJsonContext` | Same types but `WriteIndented = true` for `hina.app.json` / `registry.json` output |
| `PackageManagerCanonicalJsonContext` | Canonical bytes for `DescriptorSigner` — kept stable across versions so existing signatures keep verifying |

### Net/

| Class | Purpose |
|-------|---------|
| `SharedHttp` | Process-wide `HttpClient` singleton built on a `SocketsHttpHandler` with `PooledConnectionLifetime=60s` (forces DNS refresh on IP change), `ConnectTimeout=10s`, automatic decompression, and a `Hina/<version>` user-agent. Used by `DescriptorFetcher` and any other PM-side HTTP code path. |

### Diagnostics/

| Class | Purpose |
|-------|---------|
| `RegistryVerifier` | Reconciles the local registry against on-disk state. `Inspect` reports orphans (missing AppDir, dangling shell entries, dangling hook evidence); `RepairAsync` calls the platform `Unregister*` / `Remove*` and rewrites the registry. Used by `hina verify [--repair]`. |
| `AppDiagnostic` / `AppRepairResult` | Plain data shapes for the verifier's output. |

`InstallOptions` and `UpdateOptions` carry a `NetworkOptions` struct that
threads `MaxRetries`, `MaxRetryDelayMs`, `ConnectTimeoutMs`, and
`RequestTimeoutMs` into the `PatcherConfig` for the per-call `PatchClient`.

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

### Install Flow (Hina.PackageManager)

```
 hina install <url-to-hina.app.json>
        |
 [1] DescriptorFetcher.FetchAsync (5 MB cap, 30s timeout)
        |
 [2] DescriptorParser.Parse (source-gen JSON)
        |
 [3] DescriptorValidator.Validate (name/SemVer/HTTPS/no path traversal/entry refs)
        |
 [4] DescriptorSigner.Verify against descriptor.publicKey
        |
 [5] TOFU prompt: publisher + Ed25519 fingerprint → user accept/reject
        |
 [6] LockManager.AcquireAsync (registry-wide exclusive)
        |
 [7] If already installed → abort, suggest reinstall/update
        |
 [8] Create InstallPaths.AppDir(name) (must be empty)
        |
 [9] PatchClient.PatchAsync(appDir) — downloads chunks, verifies manifest signature with descriptor.publicKey
        |
 [10] Sanity check: descriptor.Exec[os] exists on disk
        |
 [11] CreateMenuShortcut for each entry → record evidence
        |
 [12] HookExecutor.ApplyAsync in declared order → record HookEvidence
        |
 [13] Write registry entry; cache descriptor
        |
 [14] Release lock

 On any exception in [8]-[13]: InstallTransaction.RollbackAsync unwinds
 in reverse — hooks undone, shortcuts removed, AppDir deleted, registry untouched.
```

### Update Flow (Hina.PackageManager)

```
 hina update [name]
        |
 For each app (one or all):
        |
 [1] Re-fetch descriptor from registry.descriptorUrl
        |
 [2] Validate; verify signature against REGISTRY publicKey (pin)
     A mismatch is a potential key-rotation attack → fail unless --rotate-key
     (handled by ReinstallService, not UpdateService)
        |
 [3] If descriptor.version == registry.installedVersion and not --force,
     return AlreadyUpToDate
        |
 [4] Compute diffs by stable identity:
        hooksToAdd      = descriptor.postInstall  \  registry.executedHooks
        hooksToRemove   = registry.executedHooks  \  descriptor.postInstall
        entriesToAdd / entriesToRemove similarly (by entry.id)
        |
 [5] Snapshot pre-update registry entry
        |
 [6] PatchClient.PatchAsync(appDir, Backup=true)
     On failure → PatchClient.RollbackAsync + restore registry snapshot
        |
 [7] Apply hooksToRemove (Undo), entriesToRemove
 [8] Apply hooksToAdd, entriesToAdd
        |
 [9] Update registry: installedVersion, lastUpdatedAt, hooks, entries
 [10] Refresh descriptor cache
```

### Uninstall Flow (Hina.PackageManager)

```
 hina uninstall <name>
        |
 [1] LockManager.AcquireAsync
 [2] Load registry; missing app → exit 0 (idempotent)
 [3] For each entry in registry.executedHooks (REVERSE order):
        HookExecutor.UndoAsync(evidence)            fail-soft
 [4] For each entry in registry.shellEntries:
        Platform.RemoveMenuShortcut(evidence)        fail-soft
 [5] Delete AppDir recursively                       fail-soft
 [6] Delete DescriptorCache(name)                    fail-soft
 [7] Remove app from registry, write atomically
 [8] Release lock

 Critical: hook side-effects are read from the registry, NEVER from
 the live descriptor — a newer descriptor might list different hooks.
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
