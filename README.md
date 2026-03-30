# Hina

![Hina Logo](img/Hina_Logo.png)

**An open-source, rsync-like patcher for game clients and desktop applications.**

[![Build](https://img.shields.io/badge/build-passing-brightgreen)](https://github.com)
[![.NET](https://img.shields.io/badge/.NET-10-blue)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue)](LICENSE)

Hina delivers fast, bandwidth-efficient updates by computing rolling checksums against local files and transferring only the chunks that differ. It supports Ed25519 manifest signing, Brotli-compressed chunk storage, content-defined chunking, automatic retry with exponential backoff, and structured logging -- all available as a standalone CLI or as a library you embed in your own application.

---

## Key Features

- **rsync-like delta patching** -- rolling checksum matching transfers only changed blocks.
- **Content-defined chunking (CDC)** -- FastCDC-inspired algorithm for better deduplication when files have insertions or deletions.
- **Ed25519 manifest signing** -- cryptographic verification of every manifest before patching.
- **Per-chunk and per-file hash verification** -- integrity checks at every stage.
- **Brotli-compressed chunk storage** -- reduces server bandwidth and storage.
- **Retry with exponential backoff** -- automatic recovery from transient 5xx and network errors.
- **Backup and rollback** -- automatic backups enable instant rollback on failure.
- **Concurrent downloads** -- configurable parallelism for faster patching.
- **Structured logging** -- built on `Microsoft.Extensions.Logging` with `--verbose` debug output.
- **Static hosting** -- included ASP.NET Core host, or deploy to any CDN / Nginx.
- **102 unit and integration tests** -- comprehensive test coverage across the core library.
- **Open source end-to-end** -- no proprietary dependencies or services.

---

## How It Works

Hina produces a manifest and a chunk store from a build directory. Clients download only the chunks they are missing, rebuild files locally, and verify integrity.

### Build Pipeline

```
Build artifacts (your game / app files)
        |
        v
  Hina.Builder
        |
        +--> manifest.json   (file list, hashes, chunk map, optional signature)
        |
        +--> chunks/          (Brotli-compressed blocks, stored by content hash)
        |
        v
  Static host or CDN  (Hina.Host, Nginx, S3, etc.)
```

### Client Patch Pipeline

```
Client startup
        |
        v
  Fetch manifest  -----> Verify Ed25519 signature (if configured)
        |
        v
  Rolling checksum scan of local files
        |
        v
  Download missing chunks  (concurrent, with retry + exponential backoff)
        |
        v
  Rebuild file  -->  Verify hash  -->  Swap in place
        |
        v
  Success   or   Rollback from backup
```

---

## Quick Start

```shell
# 1. Build the solution
dotnet build Hina.sln

# 2. Generate signing keys
dotnet run --project Hina.Builder -- keygen --out ./keys --name hina

# 3. Build a patch from your game directory
dotnet run --project Hina.Builder -- build \
  --input ./build \
  --out ./patch \
  --base https://patch.example.com/ \
  --version 1.0.0 \
  --sign-key ./keys/hina.key.b64

# 4. Serve the patch
dotnet run --project Hina.Host

# 5. Patch a client
dotnet run --project Hina.CLI -- patch \
  --dir ./client \
  --base https://patch.example.com/ \
  --pubkey ./keys/hina.pub.b64
```

---

## Project Layout

| Project | Description |
|---------|-------------|
| `Hina.Core` | Core library: patching engine, rsync matching, manifest handling, hashing, signing, compression, CDC |
| `Hina.CLI` | Command-line client patcher |
| `Hina.Builder` | Manifest and chunk store generator |
| `Hina.Host` | ASP.NET Core static HTTP server for serving patches |

---

## Build and Test

**Requirements:** .NET SDK 10.x

Build everything:

```shell
dotnet build Hina.sln
```

Publish the CLI for Windows and Linux:

```shell
pwsh ./scripts/publish-cli.ps1
```

Run all tests (102 unit and integration tests):

```shell
dotnet test Hina.sln
```

---

## Builder Usage

The builder scans a directory of build artifacts and produces a manifest plus a chunk store.

### Generate Signing Keys

```shell
dotnet run --project Hina.Builder -- keygen --out ./keys --name hina
```

This creates two files:

- `./keys/hina.key.b64` -- Ed25519 private key (keep secret)
- `./keys/hina.pub.b64` -- Ed25519 public key (distribute to clients)

### Build with Fixed-Size Chunking (default)

```shell
dotnet run --project Hina.Builder -- build \
  --input ./build \
  --out ./patch \
  --base https://patch.example.com/ \
  --version 1.0.0 \
  --chunk 65536 \
  --sign-key ./keys/hina.key.b64
```

### Build with Content-Defined Chunking (CDC)

CDC produces variable-size chunks based on content boundaries. This provides significantly better deduplication when files change through insertions or deletions rather than in-place modifications.

```shell
dotnet run --project Hina.Builder -- build \
  --input ./build \
  --out ./patch \
  --base https://patch.example.com/ \
  --version 1.0.0 \
  --chunking cdc \
  --min-chunk 2048 \
  --max-chunk 65536 \
  --avg-chunk 8192 \
  --sign-key ./keys/hina.key.b64
```

### Builder Commands and Flags

| Command | Description |
|---------|-------------|
| `build` | Generate manifest and chunk store from a build directory |
| `keygen` | Generate an Ed25519 key pair for manifest signing |

**Build flags:**

| Flag | Description |
|------|-------------|
| `--input` | Path to the build artifacts directory |
| `--out` | Output directory for manifest and chunks |
| `--base` | Base URL of the patch server |
| `--version` | Version string for this build |
| `--chunk` | Fixed chunk size in bytes (default: 65536) |
| `--chunking` | Chunking mode: `fixed` or `cdc` (default: `fixed`) |
| `--min-chunk` | Minimum CDC chunk size (default: 2048) |
| `--max-chunk` | Maximum CDC chunk size (default: 65536) |
| `--avg-chunk` | Average CDC chunk size (default: 8192) |
| `--sign-key` | Path to Ed25519 private key file |
| `-v`, `--verbose` | Enable debug logging output |

**Keygen flags:**

| Flag | Description |
|------|-------------|
| `--out` | Output directory for key files |
| `--name` | Base name for key files |

---

## Host Usage

Hina.Host is a lightweight ASP.NET Core static file server purpose-built for serving patch manifests and chunks.

Start the host:

```shell
dotnet run --project Hina.Host
```

### Configuration

Create a `hina.host.json` file:

```json
{
  "root": "patch"
}
```

Pass a configuration file explicitly:

```shell
dotnet run --project Hina.Host -- --config ./hina.host.json
```

### Health Check

The host exposes a `/health` endpoint for monitoring and load balancer probes.

```
GET /health
```

---

## CLI Usage

The CLI is the client-side patcher. It reads a configuration file or accepts flags directly.

### Commands

**patch** -- Download and apply all missing or changed files:

```shell
dotnet run --project Hina.CLI -- patch \
  --dir ./client \
  --base https://patch.example.com/
```

**check** -- Compare local files against the manifest without downloading:

```shell
dotnet run --project Hina.CLI -- check \
  --dir ./client \
  --base https://patch.example.com/
```

**verify** -- Verify integrity of all local files against manifest hashes:

```shell
dotnet run --project Hina.CLI -- verify \
  --dir ./client \
  --base https://patch.example.com/
```

**rollback** -- Restore files from backups if a patch failed:

```shell
dotnet run --project Hina.CLI -- rollback \
  --dir ./client \
  --base https://patch.example.com/
```

**cleanup** -- Remove leftover temporary and backup files:

```shell
dotnet run --project Hina.CLI -- cleanup \
  --dir ./client \
  --base https://patch.example.com/
```

### CLI Flags

| Flag | Description |
|------|-------------|
| `--dir` | Target directory to patch (required) |
| `--base` | Patch server base URL |
| `--channel` | Release channel (default: `stable`) |
| `--config` | Path to configuration file |
| `--pubkey` | Path to Ed25519 public key for manifest verification |
| `-v`, `--verbose` | Enable debug logging output |
| `--help` | Show help information |

---

## Configuration Reference

The CLI reads `hina.config.json` from the working directory, or from a path specified with `--config`. All properties are also available programmatically through `PatcherConfig`.

Example `hina.config.json`:

```json
{
  "baseUrl": "https://patch.example.com/",
  "channel": "stable",
  "concurrency": 4,
  "chunkSize": 65536,
  "verify": true,
  "backup": true,
  "trustedPublicKey": "BASE64_ED25519_PUBLIC_KEY",
  "maxRetries": 3,
  "retryBaseDelayMs": 1000,
  "chunkingMode": "fixed",
  "minChunkSize": 2048,
  "maxChunkSize": 65536,
  "avgChunkSize": 8192
}
```

### Full Property Reference

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `BaseUrl` | `Uri` | `http://localhost/` | Base URL of the patch server |
| `Channel` | `string` | `"stable"` | Release channel name |
| `Concurrency` | `int` | `4` | Number of concurrent chunk downloads |
| `ChunkSize` | `int` | `65536` | Fixed chunk size in bytes |
| `Verify` | `bool` | `true` | Verify file hashes after patching |
| `Backup` | `bool` | `true` | Keep backups of original files for rollback |
| `TrustedPublicKey` | `string?` | `null` | Base64-encoded Ed25519 public key for manifest signature verification |
| `MaxRetries` | `int` | `3` | Maximum retry attempts on transient errors (5xx, network failures) |
| `RetryBaseDelayMs` | `int` | `1000` | Base delay in milliseconds for exponential backoff between retries |
| `ChunkingMode` | `string` | `"fixed"` | Chunking strategy: `"fixed"` for fixed-size or `"cdc"` for content-defined chunking |
| `MinChunkSize` | `int` | `2048` | Minimum chunk size in bytes (CDC mode only) |
| `MaxChunkSize` | `int` | `65536` | Maximum chunk size in bytes (CDC mode only) |
| `AvgChunkSize` | `int` | `8192` | Target average chunk size in bytes (CDC mode only) |

**Notes:**

- `ChunkSize` (fixed mode) or the CDC size parameters must match between the builder and client configuration.
- Setting `TrustedPublicKey` enables mandatory signature verification. If the manifest signature does not match, the patch is rejected.
- Retry with exponential backoff activates automatically on HTTP 5xx responses and network-level errors. The delay doubles on each attempt: `RetryBaseDelayMs * 2^attempt`.

---

## Integrating Hina.Core in Your Application

Reference the `Hina.Core` project or NuGet package and use `PatchClient` directly:

```csharp
using Hina.Core.Configuration;
using Hina.Core.Patching;
using Microsoft.Extensions.Logging;

// Set up logging (optional -- pass null for silent operation)
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Debug);
});
var logger = loggerFactory.CreateLogger<PatchClient>();

// Configure the patcher
var config = new PatcherConfig
{
    BaseUrl = new Uri("https://patch.example.com/"),
    Channel = "stable",
    Concurrency = 4,
    Verify = true,
    Backup = true,
    TrustedPublicKey = "BASE64_ED25519_PUBLIC_KEY",
    MaxRetries = 3,
    RetryBaseDelayMs = 1000
};

// Run the patch
var client = new PatchClient(config, logger);
var result = await client.PatchAsync("./client", CancellationToken.None);
```

You can also load configuration from a JSON file:

```csharp
using Hina.Core.Configuration;

var config = PatcherConfigLoader.Load("./hina.config.json");
```

---

## Security Model

- **Manifest signing**: The builder signs the manifest with an Ed25519 private key. Clients verify the signature against a trusted public key before applying any changes.
- **Chunk-level integrity**: Every downloaded chunk is verified against its content hash before being written to disk.
- **File-level integrity**: After reconstruction, each file is verified against its full-file hash from the manifest.
- **Rollback safety**: If verification fails at any stage, the patcher restores the original file from backup.

For production deployments, always generate a key pair with `keygen`, sign manifests with `--sign-key`, and configure `TrustedPublicKey` on clients.

---

## Performance Notes

- **Rolling checksum matching** reduces bandwidth by identifying blocks that already exist locally, even if the file has been partially modified.
- **Brotli compression** on stored chunks reduces transfer size and disk usage on the patch server.
- **Concurrent downloads** (configurable via `Concurrency`) saturate available bandwidth on high-latency connections.
- **Content-defined chunking (CDC)** provides superior deduplication for files that change through insertions or deletions. Unlike fixed-size chunking, CDC boundaries are determined by file content, so inserting a byte at the beginning of a file does not invalidate every chunk. This is particularly beneficial for large binary assets, archives, and database files.
- **Hash-bucketed chunk storage** keeps file system lookups fast even in stores with millions of chunks.
- **Exponential backoff** prevents thundering-herd effects when many clients retry against a temporarily degraded server.

---

## Troubleshooting

**"Manifest signature is invalid"**
- Verify that the `TrustedPublicKey` in your configuration matches the public key corresponding to the private key used during the build.
- Ensure the manifest was not modified after signing.

**Client redownloads everything on each patch**
- Confirm that `ChunkSize` (or CDC size parameters) in the client configuration matches the values used by the builder.
- If switching between `fixed` and `cdc` chunking modes, a full redownload is expected on the first run.

**404 errors on chunk downloads**
- Confirm that the `chunks/` directory is accessible from the host root URL.
- If using a reverse proxy, verify it serves the full directory tree under the base URL.

**Patch fails with network errors**
- Hina retries transient failures automatically. Increase `MaxRetries` or `RetryBaseDelayMs` if your network is unreliable.
- Check server logs or the `/health` endpoint on `Hina.Host` to confirm the server is running.

**Verbose output for debugging**
- Pass `-v` or `--verbose` to the CLI for detailed debug-level log output showing each step of the patch process.

---

## Contributing

Contributions are welcome. To get started:

1. Fork the repository and create a feature branch.
2. Make your changes and add tests where appropriate.
3. Run `dotnet test Hina.sln` and confirm all tests pass.
4. Open a pull request against `master` with a clear description of the change.

Please keep pull requests focused on a single concern. For larger changes, open an issue first to discuss the approach.

---

## License

Hina is licensed under the [Apache License 2.0](LICENSE).
