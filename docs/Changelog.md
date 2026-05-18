# Changelog

All notable changes to Hina are documented in this file.

---

## Network Resilience (Round 3)

Targeted at flaky / mobile / changing-IP connections.

- `RetryPolicy` gains a `maxDelayMs` cap (default 30 s); exponential backoff
  no longer reaches multi-hour delays and the bit-shift no longer overflows
  at attempt 32.
- `PatcherConfig` defaults: `MaxRetries` 3 → 8. New `MaxRetryDelayMs`,
  `ConnectTimeoutMs` (10 s), `RequestTimeoutMs` (60 s),
  `PooledConnectionLifetimeMs` (60 s).
- `PatchClient` and `SharedHttp` both build their `HttpClient` on a
  `SocketsHttpHandler` so stale TCP sockets are recycled on schedule
  (forces DNS refresh after an IP change) and the TCP handshake fails fast
  on a black-holed route.
- `DescriptorFetcher` default retries 3 → 5 with its own max-delay cap.
- New `NetworkOptions` struct surfaces all four knobs through
  `InstallOptions` and `UpdateOptions`.
- New CLI flags `--retries N`, `--connect-timeout SEC`, `--request-timeout
  SEC` on `install` and `update`. `hina --help` lists them.
- Coverage: +7 NetworkResilienceTests (suite 208 → 215).

## Stability Hardening (Round 2)

Audit pass before round 3 closed six more issues.

- `UpdateService` brackets the hook + entry add loops in a try/catch that
  unwinds applied additions in reverse, best-effort re-applies the items it
  removed at the start of the update, rolls back the patch via
  `PatchClient.RollbackAsync`, and restores the previous registry snapshot.
  Previously a hook failure mid-update (e.g. `registerAutostart` perm
  denied) left the registry un-saved with the app dir patched and the
  side-effects partially applied.
- `Program.cs` wires Ctrl-C to a `CancellationTokenSource`. First press
  signals cooperative cancellation (services unwind cleanly); a second
  press lets the runtime kill the process.
- `SharedHttp` singleton replaces per-service `new HttpClient()` calls,
  killing the per-instance socket-exhaustion risk under high concurrency.
- `DescriptorFetcher` retries transient HTTP failures (5xx / network) and
  rejects non-http(s) schemes (`file://`, `ftp://`) up front.
- `UninstallService` detects when `InstallPath` is a symlink and deletes
  only the link, never walks into the target. Previously
  `Directory.Delete(recursive: true)` on a symlink would wipe the target's
  contents.
- `UpdateService.UpdateAsync` narrows its registry-lock window so
  `UpdateAllAsync` runs N updates in parallel (default 4) instead of
  serialising on the lock. CLI flag `--jobs N` overrides.
- Coverage: +9 Round2AuditTests (suite 199 → 208).

## Package-Manager Release

Hina pivots from a pure rsync-like patcher into a cross-platform package manager (Windows / Linux / macOS) built on top of the existing patching engine.

### Package-Manager Surface (new)

- **End-user CLI**: `hina install <url>`, `hina uninstall <name>`, `hina list`, `hina info <name>`, `hina which <name>`, `hina update [name]`, `hina reinstall <name>`. Per-user install (no admin / no sudo) into OS-standard directories.
- **Publisher descriptor (`hina.app.json`)**: small JSON file the publisher hosts at any URL. Carries `name`, `version`, `baseUrl`, `publicKey`, `exec`, `entries`, `postInstall`, and a self-contained Ed25519 `descriptorSignature`.
- **`hina dev sign-descriptor`**: CLI helper that signs a descriptor with an Ed25519 private key. Validates the descriptor before signing.
- **Whitelisted declarative hooks**: `addToPath`, `registerMimeType`, `registerUrlScheme`, `installFont`, `registerAutostart` — all user-scope, no arbitrary scripts (no RCE from a compromised publisher).
- **Shell integration** automatic on every OS: Start Menu shortcuts on Windows, `.desktop` files on Linux, minimal `.app` bundles on macOS.
- **TOFU signature pinning**: first install prompts the user with the publisher's name and Ed25519 key fingerprint; the key is then pinned in the local registry. `hina update` verifies against the pinned key. `hina reinstall --rotate-key` is required to accept a publisher key change.
- **Decentralized update model**: each installed app records its descriptor URL, baseUrl, channel, and public key in the local registry; `hina update` re-fetches descriptors and delta-patches via the existing rsync engine.

### Internals

- **New project**: `Hina.PackageManager` library — descriptor model, validator, signer, fetcher, install/uninstall/update/reinstall services, hook executor with reverse-order rollback, local registry with atomic JSON writes and file-locking, per-OS `IPlatformIntegration` (Windows / Linux / macOS).
- **Hina.CLI verb tree**: top-level is now end-user package commands. The original patcher commands (`check`, `patch`, `verify`, `rollback`, `cleanup`) plus the new `sign-descriptor` live under `hina dev <subcommand>`.
- **Hina.Core**: all JSON I/O migrated to `JsonSerializerContext` source-generation for NativeAOT compatibility. `PatcherConfig` switched from `init` to `set` properties for the same reason.

### Build & Release

- **NativeAOT**: `Hina.CLI` publishes as a single-file native binary (~7.5 MB on osx-arm64) with `InvariantGlobalization` and `StripSymbols`. No .NET runtime required on user machines.
- **Per-OS release matrix**: `.github/workflows/release.yml` now uses `windows-latest`/`ubuntu-latest`/`macos-latest` runners for AOT cross-compile that's impossible across OS families. `Hina.Builder` and `Hina.Host` stay plain self-contained and continue to cross-compile from a single host.
- **Publish scripts**: `scripts/publish-cli.sh` for Linux/macOS hosts, `scripts/publish-cli.ps1` for Windows hosts; each only emits artifacts the host can natively link.

### Tests

- Total suite: **186 tests** (102 Hina.Core + 84 Hina.PackageManager).
- New coverage: descriptor parsing/validation, polymorphic hook deserialization round-trip, signer sign/verify/tamper, registry atomic write + lock contention, install transaction reverse-order rollback, hook executor dispatch + undo, Linux/macOS/Windows platform integration (the cross-platform-safe portions run on every CI runner), end-to-end install/uninstall through `InstallService`/`UninstallService` with a fake `PatchClient`, update flow with hook diff and key pinning, reinstall happy path + key-rotation refusal + opt-in accept.

---

## Initial Patcher Release

Initial release of Hina as an rsync-like patcher for game clients and desktop applications.

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
