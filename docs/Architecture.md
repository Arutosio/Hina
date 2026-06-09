# Architecture

This document describes the internal architecture of Hina, including the project structure, core library design, data flow pipelines, key classes, and design decisions.

---

## Project Structure

Hina is organized into five shipped projects plus five test projects:

| Project | Type | Description |
|---------|------|-------------|
| **Hina.Core** | Class Library | Core engine: patching, rsync matching, manifest handling, chunking, hashing, signing, compression, networking, configuration, shared CLI arg parsing (`Cli/Args.cs`) |
| **Hina.PackageManager** | Class Library | Package-manager layer: descriptor schema, validator, signer/fetcher, install/uninstall/update/reinstall services, hook executor, per-OS shell integration, local registry |
| **Hina.CLI** | Console App (NativeAOT) | End-user CLI (`hina install/update/uninstall/list/info/which/reinstall/run/perms/verify`) plus developer subcommands under `hina dev <cmd>` |
| **Hina.Builder** | Console App | Manifest/chunk-store builder and interactive publish wizard (`init`) |
| **Hina.Host** | ASP.NET Core App | Lightweight static file server for serving patches (`HostOptions`, `Routing`, `AccessStats`, `SetupWizard` each in their own file; `Program.cs` is pipeline wiring only) |
| **Hina.Core.Tests** | xUnit Test Project | Unit and integration tests for the core engine |
| **Hina.PackageManager.Tests** | xUnit Test Project | Unit + cross-platform integration tests for the package-manager layer |
| **Hina.CLI.Tests** | xUnit Test Project | Command routing and arg parsing tests |
| **Hina.Builder.Tests** | xUnit Test Project | Build/keygen and init-wizard tests |
| **Hina.Host.Tests** | xUnit Test Project | Options/routing/stats unit tests plus in-process endpoint tests via `WebApplicationFactory` |

Build settings shared by every project (`TargetFramework`, `Nullable`, `ImplicitUsings`)
live in the root `Directory.Build.props`; NuGet package versions are centralized in
`Directory.Packages.props` (Central Package Management) — bump versions there, not in
the individual `.csproj` files.

### Dependency Graph

> 📊 Rendered: [System architecture](Diagrams.md#system-architecture) in [Diagrams.md](Diagrams.md).

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
| `InstalledApp` | Per-app row: pinned baseUrl/publicKey/channel, install path, descriptorUrl, executed hooks, shell entries, plus `UserGrants` (the extra filesystem paths the user has granted a sandboxed app at runtime, as resolved absolute `FsGrant` rows) |
| `FsGrant` | A user-granted absolute filesystem path for a sandboxed app: `Path` + `Access` (`ro`/`rw`). Additive and default-empty so older registries round-trip; `schemaVersion` stays 1. |
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
| `IPlatformIntegration` | All shell-touching operations: shortcuts, AddToPath, MIME, URL scheme, font, autostart (+ `Remove`/`Unregister` counterparts). Returns "evidence" strings stored in the registry. Two sandbox-related additions, both with default-impl fallbacks so platforms without a backend are unaffected: a 4-arg `CreateMenuShortcut(entry, appDir, launchOverride, ct)` overload that sets the shortcut's launch command to `launchOverride` verbatim (e.g. `hina run <app> <entry>`) instead of the app binary; and `EnumerateManagedArtifacts()`, which returns every Hina-managed artifact path on disk (independent of the registry) so `FindOrphanArtifacts` can find leftovers. |
| `PlatformIntegrationFactory` | Picks the right impl via `RuntimeInformation.IsOSPlatform` |
| `LinuxPlatformIntegration` | `.desktop` files in `~/.local/share/applications`, symlinks in `~/.local/bin`, fonts in `~/.local/share/fonts`, `~/.config/autostart/*.desktop`. The only impl that honors `launchOverride` (writes it as the `.desktop` `Exec` line) and implements `EnumerateManagedArtifacts()` (scans `hina-*` shortcuts / handlers / fonts; bin symlinks are excluded as they carry no Hina marker). |
| `WindowsPlatformIntegration` | `.lnk` shortcuts via COM `IShellLink`, `.cmd` shims in `%LOCALAPPDATA%\Hina\bin` (PATH-extended), HKCU registry for MIME/URL/autostart, per-user fonts. Falls back to the default `CreateMenuShortcut` (ignores `launchOverride`) and `EnumerateManagedArtifacts` (empty) — no sandbox enforcement yet. |
| `MacOSPlatformIntegration` | Minimal `.app` bundles in `~/Applications` with generated Info.plist, helper bundles with `CFBundleDocumentTypes` / `CFBundleURLTypes`, `~/Library/Fonts`, `~/Library/LaunchAgents/*.plist`. Same default fallbacks as Windows — no sandbox enforcement yet. |
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
| `RegistryVerifier` | Reconciles the local registry against on-disk state. `Inspect` reports orphans (missing AppDir, dangling shell entries, dangling hook evidence) **plus a local integrity check** (reads the cached descriptor and confirms the declared exec + entry files exist on disk, reporting `DescriptorCacheMissing` / `MissingFiles`); `RepairAsync` calls the platform `Unregister*` / `Remove*` and rewrites the registry. `FindOrphanArtifacts` scans `IPlatformIntegration.EnumerateManagedArtifacts()` and subtracts every registry-referenced path to surface leftovers from a manual `registry.json` deletion; `RepairOrphanArtifactsAsync` deletes them (fail-soft, under the registry lock). Used by `hina verify [--repair] [--deep]`. |
| `AppDiagnostic` / `AppRepairResult` | Plain data shapes for the verifier's output. `AppDiagnostic` carries `AppDirMissing`, `DescriptorCacheMissing`, `MissingFiles`, `DanglingShellEntries`, `DanglingHooks`, and an `IsHealthy` roll-up. |

`InstallOptions` and `UpdateOptions` carry a `NetworkOptions` struct that
threads `MaxRetries`, `MaxRetryDelayMs`, `ConnectTimeoutMs`, and
`RequestTimeoutMs` into the `PatcherConfig` for the per-call `PatchClient`.

### Io/

| Class | Purpose |
|-------|---------|
| `AtomicFile` | Crash-safe small-file text write: serializes to a sibling `.tmp`, `Flush(flushToDisk: true)`, then `File.Move(..., overwrite: true)` — atomic for a same-volume rename on POSIX and Windows. A crash mid-write leaves the old file (or no file) intact, never a truncated one. The same pattern `RegistryStore` uses for `registry.json`, centralised so the descriptor cache and any future small-file writes share it. |

### Sandbox/

Optional Flatpak-style filesystem isolation. Filesystem scope (and the `network`
capability) is **enforced on Linux (Landlock) and macOS (Seatbelt)**; **Windows is NoOp**
(an unwired experimental AppContainer backend exists — see
[Windows-Sandbox-Resume.md](Windows-Sandbox-Resume.md)). The other declared capabilities
are surfaced but not enforced, and there are no portals. See the rendered
[sandbox isolation per OS](Diagrams.md#sandbox--container-isolation-per-os) diagrams.

The architecture mirrors `Platform/`: a per-OS interface (`ISandboxLauncher`) with a factory that selects one implementation. The pivot is that **Hina becomes the launcher for sandboxed apps** — their shell shortcut's launch command points at `hina run <app> <entry>` instead of the app binary, so `hina run` can build the filesystem plan and install the sandbox before the app starts. Non-sandboxed apps continue to launch their binary directly.

| Class | Purpose |
|-------|---------|
| `SandboxPlanner` | `Build(spec, userGrants, appDir, env)` folds the descriptor's declared `FsRule`s and the user's runtime `FsGrant`s into a resolved `SandboxPlan`. The install dir is always granted read-only; a `host` rule short-circuits to `Unrestricted`; when the same concrete path appears twice, read-write wins. |
| `SandboxPlan` / `ResolvedFsRule` | `SandboxPlan { bool Unrestricted, IReadOnlyList<ResolvedFsRule> Rules }`; each `ResolvedFsRule` is an absolute `Path` + `CanWrite` flag. |
| `SandboxEnv` | Resolved user-directory roots (`Home`, `Documents`, `Download`, `Config`, `Tmp`) that abstract tokens map onto. `FromSystem()` reads the live XDG vars with home-relative fallbacks; constructed explicitly in tests for determinism. |
| `SandboxPaths` | `Resolve(token, appDir, env)` maps a `SandboxTokens` value to an absolute path. Returns `null` for `host` (means "no restriction", not a path) and for unknown tokens (already rejected by `DescriptorValidator`). |
| `ISandboxLauncher` | Per-OS launcher: `bool IsSupported` + `int Launch(execAbs, appArgs, plan, ct)`. A backend MAY replace the current process image (`execv`) rather than spawn a child — in that case `Launch` returns only on exec failure. |
| `LinuxLandlockSandbox` | Linux backend (kernel 5.13+). Unprivileged P/Invoke into `landlock_create_ruleset` / `landlock_add_rule` / `landlock_restrict_self` plus `prctl(NO_NEW_PRIVS)`, then `execv`. Builds a ruleset that denies every filesystem access right the running ABI knows and grants back only the resolved paths; restrictions apply to `hina` itself and are inherited across `execv`, so the sandboxed app's PID is hina's. ABI-probed; any failure logs a warning and execs unsandboxed — it never blocks a launch. |
| `MacOsSandbox` / `MacOsSeatbeltProfile` | macOS backend. `MacOsSeatbeltProfile.Build` generates a Seatbelt `.sb` profile from the plan (deny-default, `bsd.sb` import, system read baseline, plan rules `ro`→read / `rw`→+write, network unless `RestrictNetwork`); `MacOsSandbox` canonicalizes paths and launches the app as a **child** under `sandbox-exec -f <profile>`, cleaning up the temp profile on exit. |
| `WindowsSandbox` | Windows AppContainer backend — **EXPERIMENTAL, not wired into the factory**. The ACL plumbing is correct but the lowbox denied all granted access on CI; Windows stays NoOp until debugged on a real Windows box. See [Windows-Sandbox-Resume.md](Windows-Sandbox-Resume.md). |
| `NoOpSandbox` | Fallback for Windows or a too-old Linux kernel (no `sandbox-exec`/Landlock). Spawns the app and waits; warns once if the plan asked for real scoping it cannot enforce. `IsSupported` is `false`. |
| `SandboxLauncherFactory` | `Current(logger)` returns `LinuxLandlockSandbox` on Linux (when supported), `MacOsSandbox` on macOS, otherwise `NoOpSandbox` (Windows + unsupported hosts). |
| `AppPermissions` | Pure flat view of one app's permissions, folded from the cached descriptor (declared scope + capabilities) and the registry row (`UserGrants`). No I/O. Drives `hina perms`. |
| `PermissionsFormatter` | Renders `AppPermissions` as the `hina perms` table (all apps) and detail (one app). Every non-filesystem capability is rendered with a "declared — not enforced" caveat. |
| `SandboxDiff` | `Compute(oldSpec, newSpec)` → `{ Broadened, Added, Removed }`. A null/disabled sandbox means "unsandboxed = full access", so dropping or loosening a sandbox broadens (needs consent) while enabling or tightening it narrows (applies silently). Drives the update-flow permission-consent gate (a broadening update fails unless re-run with `--accept-new-permissions`). |

The descriptor wire model for the sandbox lives in `Descriptor/`: `SandboxSpec` (`Enabled`, `List<FsRule> Filesystem`, `CapabilitySpec? Capabilities`), `FsRule` (`Path` token + `Access` `ro`/`rw`), `CapabilitySpec` (`Network`/`Audio`/`Microphone`/`Screen`/`Input`/`Devices` bools — **declared only**), and `SandboxTokens` (the closed token set: `app`, `home`, `xdg-documents`, `xdg-download`, `xdg-config`, `tmp`, `host`). Unknown tokens are rejected at validation — fail closed.

---

## Data Flow

### Build Pipeline

The builder (`Hina.Builder`) takes a directory of application files and produces a manifest and chunk store.

> 📊 Rendered: [Build pipeline](Diagrams.md#build-pipeline-hinabuilder) in [Diagrams.md](Diagrams.md).

### Client Patch Pipeline

The client (`PatchClient`) downloads the manifest, matches local data, and applies changes.

> 📊 Rendered: [Client patch pipeline](Diagrams.md#client-patch-pipeline-patchclient) in [Diagrams.md](Diagrams.md).

### Rollback Flow

> 📊 Rendered: [Rollback flow](Diagrams.md#rollback-flow) in [Diagrams.md](Diagrams.md).

### Cleanup Flow

> 📊 Rendered: [Cleanup flow](Diagrams.md#cleanup-flow) in [Diagrams.md](Diagrams.md).

### Install Flow (Hina.PackageManager)

> 📊 Rendered: [Install flow](Diagrams.md#install-flow-installservice) in [Diagrams.md](Diagrams.md).

### Update Flow (Hina.PackageManager)

> 📊 Rendered: [Update flow](Diagrams.md#update-flow-updateservice) in [Diagrams.md](Diagrams.md).

### Uninstall Flow (Hina.PackageManager)

> 📊 Rendered: [Uninstall flow](Diagrams.md#uninstall-flow-uninstallservice) in [Diagrams.md](Diagrams.md).

### Run Flow (Hina.PackageManager)

The launch chokepoint. A sandboxed app's shell shortcut runs `hina run` instead of
the app binary, so the filesystem sandbox is installed before the app starts.

> 📊 Rendered: [Run flow](Diagrams.md#run-flow-runcommand--the-launch-chokepoint) in [Diagrams.md](Diagrams.md).

### Permissions Flow (Hina.PackageManager)

> 📊 Rendered: [Permissions flow](Diagrams.md#permissions-flow-hina-perms--verify) in [Diagrams.md](Diagrams.md).

`AppPermissions.From(cachedDescriptor, registryRow)` folds the declared sandbox scope
and the registry `UserGrants` into a flat view; `PermissionsFormatter` renders it.
Filesystem is the only enforced category; every other capability is shown "declared —
not enforced".

`hina verify [name] [--repair] [--deep]` extends the diagnostic: the default offline
pass adds local integrity checks (descriptor cache + declared files present) and a global
orphan-artifact scan; `--repair` (also reachable as `hina repair`) prunes orphan entries,
dangling side-effects, and orphan artifacts; `--deep` re-fetches the manifest and hashes
every file via `PatchClient.VerifyAsync`. `hina dev sandbox-run --app-dir <dir>
[--allow <path>[:ro|:rw] ...] [--host] -- <exec> [args...]` applies a filesystem sandbox
(Landlock on Linux) then execs — it drives the Landlock integration test and is handy for
manual sandbox probing.

---

## Class Diagram

> 📊 Rendered: [Class diagram](Diagrams.md#class-diagram--core--patching) in [Diagrams.md](Diagrams.md).

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
