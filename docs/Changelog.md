# Changelog

All notable changes to Hina are documented in this file.

---

## Current Version

Initial release of Hina, an rsync-like patcher for game clients and desktop applications.

### Core Patching Engine

- **Rsync-like delta patching** -- Rolling checksum (Adler32-style) matching identifies reusable chunks in local files, minimizing download size. Only changed chunks are transferred from the server.
- **Content-defined chunking (CDC)** -- Gear hash-based chunking with configurable min/max/avg chunk sizes. Provides superior deduplication compared to fixed-size chunking when files change through insertions or deletions.
- **Fixed-size chunking** -- Traditional fixed-block chunking as an alternative to CDC. Simple and predictable for workloads with uniform change patterns.
- **Brotli-compressed chunk storage** -- All chunks are compressed with Brotli before storage and transfer, reducing bandwidth and disk usage.
- **SHA256 file and chunk hashing** -- Every chunk carries a SHA256 strong hash for integrity verification. Reconstructed files are verified against a full-file SHA256 hash.

### Reliability

- **Retry with exponential backoff** -- Transient HTTP errors (5xx, timeouts, network failures) trigger automatic retries with exponential backoff and jitter. Configurable max retries and base delay.
- **Journaled patch sessions** -- A patch journal tracks backup entries during each session. Interrupted patches are detected and rolled back automatically on the next run.
- **Atomic file replacement** -- Files are reconstructed into a temporary file (`.hina.tmp`) and swapped into place only after verification passes.
- **Backup and rollback** -- Original files are backed up (`.hina.bak`) before replacement, enabling rollback to the previous state on failure or on demand.
- **Post-patch verification** -- Optional (enabled by default) SHA256 verification of every patched file immediately after reconstruction.

### Security

- **Ed25519 manifest signing** -- Manifests can be signed with an Ed25519 private key during the build. Clients verify signatures against a trusted public key, rejecting tampered or unsigned manifests.
- **Key generation** -- Built-in `keygen` command generates Ed25519 key pairs as Base64-encoded files.

### Tooling

- **Hina.Builder** -- Standalone build tool that scans an input directory, computes chunk maps, writes Brotli-compressed chunks, and produces a signed manifest. Supports both fixed and CDC chunking modes.
- **Hina.CLI** -- Command-line client with `check`, `patch`, `verify`, `rollback`, and `cleanup` commands. Supports config files, command-line overrides, and verbose logging.
- **Hina.Host** -- Lightweight ASP.NET Core static file server for serving manifests and chunks. Includes a `/health` endpoint for monitoring.

### Architecture

- **Hina.Core library** -- All patching logic is in a standalone library with a clean `IPatchClient` interface, suitable for embedding in game launchers, desktop applications, or custom tooling.
- **Structured logging** -- Full integration with `Microsoft.Extensions.Logging` across all components. Supports any logging provider (console, Serilog, NLog, etc.).
- **Configurable via JSON** -- All settings are configurable through JSON files with sensible defaults. The CLI supports `--config` for explicit file paths and automatic discovery of `hina.config.json`.
- **.NET 10 target** -- Built on .NET 10 for latest runtime performance and language features.

### Testing

- **Comprehensive test suite** -- 102 tests covering core functionality including chunking, hashing, manifest serialization, signing/verification, retry logic, rsync matching, patch journal, cleanup, HTTP client, and end-to-end integration.
