# Changelog

All notable changes to Hina are documented in this file.

---

## v1.4.3 — host proxy support, installer hardening, scoop fix

11 fixes from bug-hunt rounds 9–10 (PR #30, PR #31). Test suite: 615 → 629 tests.

### Host (hina-host)

- **Per-client rate limiting behind a remote reverse proxy.** `X-Forwarded-For` was
  never trusted from a non-loopback address, so behind a remote proxy/LB every client
  shared the proxy's IP — one rate-limit bucket for everyone and mass 429 once their
  combined traffic crossed the limit. New `trustedProxies` json key / `--trusted-proxies`
  flag opts specific proxy IPs in (validated at startup). Default unchanged: remote
  `X-Forwarded-For` stays untrusted, so a spoofed loopback client IP still cannot reach
  the loopback-only `/stats`. Same-machine proxies keep working out of the box.
- **The setup wizard no longer destroys a corrupt config.** A hand-maintained
  `hina.host.json` with one JSON typo counted as "missing or empty", so an interactive
  start auto-ran the wizard and overwrote the file — apps/cors/rate-limit setup lost.
  Corrupt non-empty files now surface the parse error with the file named instead.
- A config whose root is not a JSON object (e.g. `[1,2,3]`) failed startup with a raw
  exception; now an actionable error.

### Client (hina)

- **Ctrl+C in the commit window no longer fakes a failed update.** Cancellation between
  the end of the add-phase and the final registry write was reported as "Files updated
  but the registry could not be saved: The operation was canceled." with files at v2 and
  the registry at v1. The commit point now always completes.
- Same class in `uninstall`: cancellation after the (already irreversible) directory
  delete left a ghost registry row pointing at a deleted directory.
- **An update that renames the app is refused.** A re-fetched descriptor with a different
  `name` used to update fine, leaving the registry row keyed by the old name with a
  cached descriptor claiming the new one (silent identity drift). The error names both
  names and the recovery path.

### Installers (curl|sh / PowerShell)

- **install.sh could destroy the user's shell rc file.** When the backup copy of
  `.zshrc`/`.bashrc` failed (unreadable file, disk full), the PATH-setup step replaced
  the entire file with just the hina block. The copy must now succeed or the file is
  skipped with a manual-add hint.
- **Checksum verification can no longer be skipped by a network error.** Both installers
  treated any failure fetching the `.sha256` as "not published" and fell back to a
  structural archive check — a transient outage (or a MITM blocking just the checksum)
  silently downgraded the install to unverified. Only a real HTTP 404 (old releases) may
  skip now; `HINA_NO_CHECKSUM=1` remains the explicit bypass.
- **install.ps1 is idempotent again.** The non-interactive version check compared
  `"hina 1.4.2"` against `"v1.4.2"` and could never match: every scripted re-run
  re-downloaded and reinstalled, and an installed build newer than the target was
  silently downgraded. Numeric semver compare now, mirroring install.sh.
- install.sh: the fish PATH line now quotes the install dir (a directory with a space
  split into two PATH entries).

### Packaging

- **`scoop install` works.** The manifest declared an `extract_dir` that does not exist
  in the release zips (the binary sits at the archive root), so scoop could never
  produce a working shim. The manifest is regenerated with this release.

---

## v1.4.2 — reliability and robustness fixes from bug-hunt rounds 7–8

24 user-reachable bug fixes from two systematic bug-hunt rounds (PR #26, PR #28).
Test suite: 540 → 615 tests.

### Critical

- **Update no longer hangs forever on a negative `retryBaseDelayMs`.** A config value
  of `-1` reached `Task.Delay(-1)` (infinite delay) on the first retry; `-1000` crashed
  with an out-of-range exception. Negative retry delays are now clamped at zero.
- **Ctrl+C no longer destroys an installed app.** Cancelling during `uninstall` (or the
  uninstall phase of `reinstall`) swallowed the cancellation and deleted files anyway;
  cancelling `update --all` mid-app reported a bogus failure instead of aborting.
  Cancellation now propagates end-to-end (services and CLI) and rollback completes
  before the abort.
- **`reinstall` validates the new version before uninstalling the old one.** A broken
  (but signed) descriptor or a raised `minHinaVersion` used to be discovered only
  after the working copy was already gone, leaving no app installed.

### Fixed — client (hina)

- Corrupted-chunk downloads (CDN edge serving truncated/garbage data) are now retried
  as transient instead of failing the install on the first bad chunk.
- A crash-interrupted update no longer leaves a corrupted patch journal that blocks
  the next run; recovery handles truncated/garbage journal files.
- Manifest or descriptor URLs answered with HTML (captive portal, misconfigured proxy)
  produce an actionable error naming the URL instead of a raw JSON parse crash.
- `update --all` stops at the first Ctrl+C instead of marching through remaining apps.
- A `hina.config.json` with missing/empty paths or out-of-range numeric values is
  reported with the offending file and key instead of crashing.
- `perms --grant ':rw'` and similar malformed grants produce a usage error naming
  `--grant`; `update`/`reinstall`/`perms` now reject unknown flags (typos like
  `--isnecure` were silently ignored — security-relevant for `--insecure`).
- A trailing valued flag with no value (`hina install <url> --retries`) fails loudly
  instead of being silently dropped.

### Fixed — host (hina-host)

- Startup validation: corrupted config JSON, invalid `--port`, negative rate limits
  (which made **every** request fail with 500), `summaryIntervalSeconds < 1`
  (busy-spin CPU), and empty/slash-containing app names now exit with an actionable
  message instead of crashing or serving broken responses.
- Access statistics no longer grow unbounded (memory leak under long uptimes).

### Fixed — builder (hina-builder)

- `--out` inside (or equal to) `--input` is rejected — the next build used to chunk
  the output store into the manifest itself, so clients downloaded the store as if it
  were the app.
- `--chunk 0`, malformed `--base` URLs, an unreadable `--sign-key`, and a truncated
  hash in an existing store now produce actionable errors instead of crashes or
  corrupt output; the init wizard validates its port prompt.

---

## v1.4.1 — delta-update and chunk-serving fixes, patch-path performance

### Fixed

- **Delta updates of existing files no longer fail with a sharing violation.** Patching
  a file that reused local chunks (rsync match) kept a read handle open across the final
  file swap, so every in-place delta update of a real-sized file failed with
  "file is being used by another process". The handle is now released before the swap.
- **Hina.Host actually serves chunks.** ASP.NET's static-file middleware rejects unknown
  extensions, and `.br` has no registered content type, so every `*.chunk.br` request
  returned 404. The host now maps `.br` to `application/octet-stream`; other unknown
  extensions (e.g. a stray `.key` left in a patch root) remain unserved.

### Performance

- Whole-file verification (`verify: true`) hashes the rebuilt file incrementally while
  it is written instead of re-reading it from disk afterwards — one full I/O pass saved
  per patched file.
- Matched-chunk copies reuse pooled buffers; chunk downloads skip a full in-memory copy
  before decompression.

### Internal

- Build settings centralized in `Directory.Build.props`; NuGet versions managed via
  Central Package Management (`Directory.Packages.props`).
- Shared CLI arg parsing moved to `Hina.Core/Cli/Args.cs` (the builder's duplicate
  parser was removed).
- `Hina.Host` split into testable units (`HostOptions`, `Routing`, `AccessStats`,
  `SetupWizard`) with a new `Hina.Host.Tests` suite (25 tests, in-process endpoint
  tests). Total suite: 540 tests.
- CI: GitHub Actions bumped to v4 with NuGet package caching.

---

## v1.4.0 — multi-platform variants, publish wizard, edge-case hardening

### Per-platform variants (selective download)

- A descriptor can declare a `platforms` array (`{os, arch?, exec}`) so a
  cross-platform app ships one `manifest.<os>[-<arch>].json` per variant over a
  **shared** content-addressed chunk store. A client downloads **only** the
  variant matching its machine instead of every platform's files.
- The client picks the most specific `(os, arch)` match, falls back to the `x64`
  build on an arm64 host (Rosetta on macOS, emulation on Windows) with a warning,
  and errors cleanly when no variant serves the OS. The installed variant token is
  recorded so `update`/`verify`/`run` refetch the same manifest.
- `hina-builder build --platform <token>` writes `manifest.<token>.json` into a
  shared `--out` chunk store (dedup across variants).
- Backward-compatible: apps with no `platforms` keep the legacy single-manifest
  (`manifest.json`, OS `exec` map) behavior unchanged.

### Publish wizard

- New `hina-builder init`: an interactive wizard that scans the app folder, detects
  executables by magic bytes (PE/ELF/Mach-O, `.app` bundles), pre-fills every prompt
  with smart defaults (from an existing `hina.app.json` — re-run = edit — or from
  `.csproj`/`package.json`/Unity/Godot project files), asks a few plain-language
  sandbox questions, generates an Ed25519 key pair if needed, and writes a signed
  `hina.app.json` plus the manifest/chunk store. Detects per-variant subfolders and
  builds every variant automatically. The signing key and patch store are written
  **outside** the scanned payload so they're never shipped to users.

### Edge-case hardening (user-triggerable)

- `uninstall` no longer drops the registry row when the install-dir delete fails
  (locked/read-only files) — the app stays listed and the uninstall is retryable
  instead of leaving invisible orphaned files.
- The CLI rejects unknown/typo flags (e.g. `install … --allow-insecue`) and invalid
  numeric flag values (`--retries 0/-5/abc`, `--jobs abc`) instead of silently
  ignoring them; `perms <app> --grant` with no path is now a usage error.
- A corrupt (non-empty, unparseable) `registry.json` surfaces an actionable error
  instead of a raw JSON parser exception.

---

## v1.3.1 — help fix

- `hina` with no args now lists the sandbox-era verbs **run**, **perms**, and
  **repair** in the main help (they were missing from `Help.PrintMain`).

---

## v1.3.0 — macOS & network enforcement, tech debt, Windows investigation

Builds on the v1.2.0 sandbox (Linux/Landlock, filesystem-only) by enforcing on a
second OS and adding network enforcement, plus tech-debt cleanup and a documented
(but not-yet-working) Windows backend.

### Sandbox enforcement broadened

- **macOS filesystem + network enforcement** via `sandbox-exec` (Seatbelt): `hina run`
  generates a deny-default `.sb` profile from the declared scope and launches the app
  under it. macOS is no longer "declared-only".
- **`network` capability enforced** on Linux 6.7+ (Landlock ABI ≥ 4) and macOS: a
  sandboxed app that doesn't declare `network: true` has outbound network denied.
- **Implicit system-runtime grants** so dynamically-linked apps actually start under
  Landlock (loader/libc/device nodes), with a device-node access-rights fix.
- **Capability disclosure** at install time and in `hina info` / `hina perms`; non-
  filesystem capabilities are clearly shown as "declared — not enforced".

### Windows sandbox — investigated, NOT enforced (stays NoOp)

- An AppContainer backend (`WindowsSandbox`) is implemented and its ACL plumbing is
  proven correct on CI, but the lowbox denied all granted access on the runner, so it
  is **not wired in** — Windows keeps the honest NoOp + install warning. Full
  investigation and resume steps in `docs/Windows-Sandbox-Resume.md`.

### Tech debt & internals

- Registry **schema-migration framework** (forward-upgrade `registry.json` on load).
- `UpdateService` refactor (extracted rollback + cache-refresh); shared
  `PlatformText.StripControl`; expanded CLI router test coverage.

### Docs

- New **`docs/Diagrams.md`**: 20 Mermaid diagrams (architecture, class diagrams, every
  pipeline, and the sandbox/container isolation flow per OS).

---

## Sandboxing, Permissions & Integrity

Apps can now opt into filesystem isolation, users can inspect and grant
permissions, and the install can be checked and repaired.

### Sandbox (v1)

- **Optional `sandbox` block** in the signed `hina.app.json`: a filesystem scope
  (abstract tokens `app`, `home`, `xdg-documents`, `xdg-download`, `xdg-config`,
  `tmp`, `host`, each `ro`/`rw`) plus declared capabilities (`network`, `audio`,
  `microphone`, `screen`, `input`, `devices`).
- **Filesystem enforcement on Linux only**, via Landlock (unprivileged, kernel
  ≥ 5.13, no root / no bubblewrap). Old kernel / no Landlock → no-op with a
  one-time warning, never blocks the launch. macOS/Windows: scope is **declared
  but NOT enforced** — install warns the app runs with full user privileges.
- **Capabilities are declared-only**, never enforced yet (no portals). `hina
  perms` shows them as "declared — not enforced".
- **`hina run <app> [entryId] [-- args]`**: the launch chokepoint. Sandboxed apps'
  shortcuts route through it so the Landlock ruleset is installed before `execv`.
- The `host` token grants unrestricted access and is surfaced loudly at install
  and in `hina perms`.

### Permissions

- **`hina perms`** (aliases `permissions` / `permessi`): table of all apps'
  permissions, per-app detail view, and `--grant <path>[:ro|:rw]` / `--revoke
  <path>` to manage user filesystem grants (persisted in the registry, folded into
  the Landlock ruleset at launch).
- **Update permission consent**: an update that *broadens* an app's access (new
  path, `host`, `ro → rw`, a new capability, or removing the sandbox) is refused
  before any file is touched until `hina update --accept-new-permissions`.
  Narrowing applies automatically.

### Integrity & Repair

- **`hina verify [name]`**: offline check that each per-OS exec, `entries[].exec`,
  and the descriptor cache are present. Missing files point the user at `hina
  reinstall`.
- **`hina verify --deep`**: re-fetches the manifest and hash-verifies every
  installed file against it (network required).
- **`hina repair`** (= `hina verify --repair`): removes orphan registry rows,
  dangling shortcuts/hooks, and true-orphan artifacts left after a manual
  `registry.json` deletion. Idempotent.
- **`hina uninstall <name>`** is now fail-soft — it works even when the install
  directory is already gone.

### Hardening

- `entries[].id` is charset-validated against `^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$`.
  The id flows into the `.desktop` `Exec=` line (`hina run <app> "<id>"`), so this
  closes a command-injection surface for a signed-but-hostile descriptor.
- Descriptor-cache writes are atomic.
- `Registry.InstalledApp` gained a `userGrants` list (schemaVersion still `1`;
  older registries round-trip unchanged).

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
