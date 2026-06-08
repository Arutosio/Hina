# Diagrams

The visual companion to [Architecture.md](Architecture.md): the project graph, class
diagrams, every data-flow pipeline, and the sandbox / container-isolation flow per OS —
all in [Mermaid](https://mermaid.js.org/) (GitHub renders these natively). This file is
the single source of truth for diagrams; the prose docs link here.

Sandbox state reflected below (current code): filesystem + network are **enforced on
Linux (Landlock) and macOS (Seatbelt)**; **Windows is NoOp** (an unwired, experimental
AppContainer backend exists — see [Windows-Sandbox-Resume.md](Windows-Sandbox-Resume.md)).

## Table of contents

- [System architecture](#system-architecture)
- [CLI command routing](#cli-command-routing)
- [Class diagram — Core / patching](#class-diagram--core--patching)
- [Class diagram — PackageManager](#class-diagram--packagemanager)
- [Class diagram — Sandbox](#class-diagram--sandbox)
- [Pipelines](#pipelines) — Build, Client Patch, Rollback, Cleanup, Install, Update, Uninstall, Run, Permissions
- [Sandbox / container isolation per OS](#sandbox--container-isolation-per-os)

---

## System architecture

The five shipped projects plus two test projects and their references. `Hina.CLI`
references both `Hina.PackageManager` (top-level commands) and `Hina.Core` directly
(the `hina dev` patcher subcommands). `Hina.PackageManager` reuses
`Hina.Core.PatchClient` for delta downloads — there is no parallel engine.

```mermaid
graph LR
    CLI["Hina.CLI<br/>(NativeAOT console)"]
    PM["Hina.PackageManager<br/>(library)"]
    Core["Hina.Core<br/>(library: patch engine)"]
    Builder["Hina.Builder<br/>(console)"]
    Host["Hina.Host<br/>(ASP.NET static server)"]
    CoreT["Hina.Core.Tests"]
    PMT["Hina.PackageManager.Tests"]

    CLI --> PM
    CLI --> Core
    PM --> Core
    Builder --> Core
    PMT --> PM
    PMT --> Core
    CoreT --> Core
    Host -.->|"serves chunks/manifests"| Core

    classDef ship fill:#dbeafe,stroke:#1e40af,color:#1e3a8a;
    classDef test fill:#f3f4f6,stroke:#9ca3af,color:#374151;
    class CLI,PM,Core,Builder,Host ship;
    class CoreT,PMT test;
```

---

## CLI command routing

`Program.Main` parses global flags (`--verbose`, `--version`, `help`) then hands off to
`CommandRouter`, which dispatches on the first arg to a command that calls into a
`Hina.PackageManager` service (or `Hina.Core` for `hina dev`). Source:
`Hina.CLI/Program.cs`, `Hina.CLI/CommandRouter.cs`, `Hina.CLI/Commands/*`.

```mermaid
flowchart TD
    P["Program.Main(args)"] --> R{"CommandRouter<br/>dispatch on args[0]"}
    R -->|install| INS["InstallService"]
    R -->|update| UPD["UpdateService"]
    R -->|reinstall| REI["ReinstallService"]
    R -->|uninstall| UNI["UninstallService"]
    R -->|run| RUN["RunCommand → SandboxPlanner + SandboxLauncherFactory"]
    R -->|"perms / permissions / permessi"| PER["PermsCommand → AppPermissions / PermissionsFormatter"]
    R -->|"verify / repair"| VER["RegistryVerifier"]
    R -->|"list / info / which"| RO["read-only registry / descriptor cache"]
    R -->|dev| DEV["DevCommand: check / patch / verify / rollback / cleanup / sign-descriptor / sandbox-run"]
    DEV --> CoreEngine["Hina.Core.PatchClient / DescriptorSigner / SandboxLauncherFactory"]
```

---

## Class diagram — Core / patching

The patch engine in `Hina.Core`. `PatchClient` implements `IPatchClient`, drives an
`IChunker` + `IHasher`, fetches via `HttpChunkClient` (with `RetryPolicy`), and journals
backups in `PatchJournal`. `ManifestSigner` signs/verifies the `Manifest` tree.

```mermaid
classDiagram
    class IPatchClient {
        <<interface>>
        +CheckAsync()
        +PatchAsync()
        +VerifyAsync()
        +RollbackAsync()
    }
    class PatchClient {
        -IHasher _hasher
        -HttpChunkClient _http
        -RsyncMatchLocal()
        -VerifyManifest()
    }
    class IChunker {
        <<interface>>
        +ChunkAsync()
    }
    class RsyncChunker
    class ContentDefinedChunker
    class IHasher {
        <<interface>>
        +AlgorithmId
        +ComputeHashAsync()
    }
    class Sha256Hasher
    class HttpChunkClient {
        +GetManifestAsync()
        +GetChunkAsync()
    }
    class RetryPolicy {
        +ExecuteAsync()
        +IsTransient()
    }
    class PatchJournal {
        +Status
        +Entries
    }
    class Manifest {
        +Version
        +BuildId
        +Files
        +Signature
    }
    class ManifestFile
    class ManifestChunk
    class ManifestSigner {
        +AttachSignature()
        +Verify()
    }

    IPatchClient <|.. PatchClient
    IChunker <|.. RsyncChunker
    IChunker <|.. ContentDefinedChunker
    IHasher <|.. Sha256Hasher
    PatchClient --> IChunker : uses
    PatchClient --> IHasher : uses
    PatchClient --> HttpChunkClient : uses
    PatchClient --> PatchJournal : journals
    HttpChunkClient --> RetryPolicy : retries with
    PatchClient ..> Manifest : matches against
    Manifest "1" --> "*" ManifestFile : contains
    ManifestFile "1" --> "*" ManifestChunk : contains
    ManifestSigner ..> Manifest : signs / verifies
```

---

## Class diagram — PackageManager

The package-manager layer. The install/update/uninstall/reinstall services orchestrate
`DescriptorFetcher`/`Validator`/`Signer`, the `RegistryStore`, the `HookExecutor`, and
the per-OS `IPlatformIntegration`; `InstallTransaction` journals side-effects for
rollback. The services reuse `Hina.Core.PatchClient` for the actual file delta.

```mermaid
classDiagram
    class InstallService
    class UpdateService
    class UninstallService
    class ReinstallService
    class InstallTransaction {
        +RollbackAsync()
    }
    class DescriptorFetcher
    class DescriptorValidator
    class DescriptorSigner
    class AppDescriptor {
        +Name
        +Version
        +Exec
        +Entries
        +PostInstall
        +Sandbox
    }
    class RegistryStore {
        +LoadAsync()
        +SaveAsync()
    }
    class Registry
    class InstalledApp {
        +InstallPath
        +ExecutedHooks
        +ShellEntries
        +UserGrants
    }
    class HookExecutor {
        +ApplyAsync()
        +UndoAsync()
    }
    class IPlatformIntegration {
        <<interface>>
        +CreateMenuShortcut()
        +RegisterMimeType()
        +InstallFont()
    }
    class LinuxPlatformIntegration
    class MacOSPlatformIntegration
    class WindowsPlatformIntegration
    class PlatformIntegrationFactory

    InstallService --> DescriptorFetcher : fetch
    InstallService --> DescriptorValidator : validate
    InstallService --> DescriptorSigner : verify
    InstallService --> RegistryStore : commit
    InstallService --> HookExecutor : apply
    InstallService --> InstallTransaction : journals
    UpdateService --> RegistryStore
    UninstallService --> HookExecutor : undo
    ReinstallService --> InstallService
    ReinstallService --> UninstallService
    DescriptorFetcher ..> AppDescriptor : produces
    RegistryStore --> Registry
    Registry "1" --> "*" InstalledApp
    HookExecutor --> IPlatformIntegration : dispatches to
    IPlatformIntegration <|.. LinuxPlatformIntegration
    IPlatformIntegration <|.. MacOSPlatformIntegration
    IPlatformIntegration <|.. WindowsPlatformIntegration
    PlatformIntegrationFactory ..> IPlatformIntegration : selects per OS
```

---

## Class diagram — Sandbox

The sandbox subsystem mirrors `Platform/`: a per-OS `ISandboxLauncher` chosen by
`SandboxLauncherFactory`. `SandboxPlanner` folds the descriptor scope + user grants into
a `SandboxPlan` (resolving abstract `SandboxTokens` to absolute paths via `SandboxEnv` /
`SandboxPaths`). `AppPermissions` / `PermissionsFormatter` drive `hina perms`;
`SandboxDiff` drives the update-consent gate.

```mermaid
classDiagram
    class ISandboxLauncher {
        <<interface>>
        +bool IsSupported
        +int Launch(execAbs, appArgs, plan, ct)
    }
    class LinuxLandlockSandbox
    class MacOsSandbox
    class WindowsSandbox
    class NoOpSandbox
    class SandboxLauncherFactory {
        +Current(logger) ISandboxLauncher
    }
    class MacOsSeatbeltProfile {
        +Build(plan) string
    }
    class SandboxPlanner {
        +Build(spec, grants, appDir, env) SandboxPlan
    }
    class SandboxPlan {
        +bool Unrestricted
        +IReadOnlyList~ResolvedFsRule~ Rules
        +bool RestrictNetwork
    }
    class ResolvedFsRule {
        +string Path
        +bool CanWrite
    }
    class SandboxEnv
    class SandboxPaths
    class SandboxTokens
    class AppPermissions
    class PermissionsFormatter
    class SandboxDiff {
        +Compute(oldSpec, newSpec)
    }

    ISandboxLauncher <|.. LinuxLandlockSandbox
    ISandboxLauncher <|.. MacOsSandbox
    ISandboxLauncher <|.. WindowsSandbox
    ISandboxLauncher <|.. NoOpSandbox
    SandboxLauncherFactory ..> ISandboxLauncher : Linux→Landlock, macOS→MacOs, else NoOp
    MacOsSandbox --> MacOsSeatbeltProfile : generates .sb
    SandboxPlanner --> SandboxPlan : produces
    SandboxPlan "1" --> "*" ResolvedFsRule
    SandboxPlanner ..> SandboxPaths : resolves tokens
    SandboxPaths ..> SandboxEnv : roots
    SandboxPaths ..> SandboxTokens : closed set
    ISandboxLauncher ..> SandboxPlan : consumes
    AppPermissions ..> PermissionsFormatter : rendered by
```

---

## Pipelines

Faithful flowchart versions of the nine data-flow pipelines described in prose in
[Architecture.md](Architecture.md). Step numbers match the prose.

### Build pipeline (`Hina.Builder`)

```mermaid
flowchart TD
    A["Input directory (app/game files)"] --> B["1. Enumerate files recursively"]
    B --> C["2. Per file: chunk (IChunker)<br/>weak rolling + strong SHA-256 per chunk<br/>+ full-file SHA-256 → ManifestFile"]
    C --> D["3. Assemble Manifest (Version, BaseUrl, BuildId, Files)"]
    D --> E["4. Optional: Ed25519-sign canonical manifest bytes"]
    E --> F["5. Write manifest.json"]
    F --> G["6. Brotli-compress chunks → chunks/&lt;bucket&gt;/&lt;hash&gt;.chunk.br<br/>(bucket = first 2 hex of hash; dedup)"]
    G --> H["Output: manifest.json + chunks/"]
```

### Client patch pipeline (`PatchClient`)

```mermaid
flowchart TD
    A["1. Fetch manifest.json (retry w/ backoff)"] --> B["2. Verify Ed25519 signature (if pinned key)"]
    B --> C{"3. Incomplete previous patch?<br/>(.hina/journal.json)"}
    C -->|yes| C1["Rollback previous patch first"] --> D
    C -->|no| D["4. Create new PatchJournal"]
    D --> E["5. Per file: hash local"]
    E --> F{"hash matches manifest?"}
    F -->|yes| E
    F -->|no| G["Rsync match: slide rolling checksum;<br/>confirm weak hits with strong hash"]
    G --> H["Rebuild to .hina.tmp:<br/>matched chunks copied locally,<br/>missing chunks downloaded + Brotli-decompressed"]
    H --> I["Verify rebuilt hash; backup → .hina.bak; swap into place"]
    I --> E
    E --> J{"all files done?"}
    J -->|success| K["6. Mark journal Completed"]
    J -->|failure| L["Rollback from backups; mark journal Failed"]
```

### Rollback flow

```mermaid
flowchart TD
    A["1. Load .hina/journal.json"] --> B["2. Per entry: copy .hina.bak → original; delete .hina.bak"]
    B --> C["3. Delete journal file"]
```

### Cleanup flow

```mermaid
flowchart TD
    A["1. Recursively scan target dir"] --> B["2. Delete *.hina.tmp"]
    B --> C["3. Delete *.hina.bak"]
    C --> D["4. Delete .hina/journal.json"]
```

### Install flow (`InstallService`)

```mermaid
flowchart TD
    S["hina install &lt;url-to-hina.app.json&gt;"] --> A1["1. DescriptorFetcher.FetchAsync (5 MB cap, 30s)"]
    A1 --> A2["2. DescriptorParser.Parse"]
    A2 --> A3["3. DescriptorValidator.Validate<br/>(name / SemVer / HTTPS / no traversal / entry refs)"]
    A3 --> A4["4. DescriptorSigner.Verify vs descriptor.publicKey"]
    A4 --> A5["5. TOFU prompt: publisher + Ed25519 fingerprint"]
    A5 --> A6["6. LockManager.AcquireAsync"]
    A6 --> A7{"7. already installed?"}
    A7 -->|yes| A7x["abort → suggest reinstall/update"]
    A7 -->|no| A8["8. Create empty AppDir"]
    A8 --> A9["9. PatchClient.PatchAsync (verifies manifest sig)"]
    A9 --> A10["10. Sanity: Exec[os] exists on disk"]
    A10 --> A11["11. CreateMenuShortcut per entry<br/>(sandboxed → launchOverride 'hina run'; else binary)"]
    A11 --> A12["12. HookExecutor.ApplyAsync in order → HookEvidence"]
    A12 --> A13["13. Cache descriptor (AtomicFile) THEN commit registry"]
    A13 --> A14["14. Release lock"]
    A8 -.->|"exception in 8-13"| RB["InstallTransaction.RollbackAsync<br/>(unwind in reverse; registry untouched)"]
    A13 -.->|exception| RB
```

### Update flow (`UpdateService`)

```mermaid
flowchart TD
    S["hina update [name]"] --> B1["1. Re-fetch descriptor from registry.descriptorUrl"]
    B1 --> B2["2. Validate + verify sig vs REGISTRY pinned key<br/>(mismatch = possible key rotation → fail, needs reinstall --rotate-key)"]
    B2 --> B3{"3. version unchanged and not --force?"}
    B3 -->|yes| B3x["AlreadyUpToDate"]
    B3 -->|no| B4["4. Diff hooks + entries by stable identity (add / remove)"]
    B4 --> B5["5. Snapshot pre-update registry row"]
    B5 --> B5a{"5a. SandboxDiff broadens access<br/>and not --accept-new-permissions?"}
    B5a -->|yes| B5x["fail BEFORE touching disk"]
    B5a -->|no| B6["6. PatchClient.PatchAsync (Backup=true)<br/>fail → Rollback + restore registry snapshot"]
    B6 --> B7["7. Apply removals (hooks Undo, entries)"]
    B7 --> B8["8. Apply additions (hooks, entries)"]
    B8 --> B9["9. Update registry (version, hooks, entries)"]
    B9 --> B10["10. Refresh descriptor cache"]
```

### Uninstall flow (`UninstallService`)

```mermaid
flowchart TD
    S["hina uninstall &lt;name&gt;"] --> C1["1. LockManager.AcquireAsync"]
    C1 --> C2{"2. app present?"}
    C2 -->|no| C2x["exit 0 (idempotent)"]
    C2 -->|yes| C3["3. HookExecutor.UndoAsync per hook, REVERSE order (fail-soft)"]
    C3 --> C4["4. Platform.RemoveMenuShortcut per entry (fail-soft)"]
    C4 --> C5["5. Delete AppDir (fail-soft)"]
    C5 --> C6["6. Delete descriptor cache (fail-soft)"]
    C6 --> C7["7. Remove from registry, write atomically"]
    C7 --> C8["8. Release lock"]
```

> Hook side-effects are read from the **registry**, never the live descriptor (a newer
> descriptor might list different hooks).

### Run flow (`RunCommand` — the launch chokepoint)

```mermaid
flowchart TD
    S["hina run &lt;app&gt; [entryId] [-- appArgs]"] --> D1["1-2. Load registry; AppDir present?"]
    D1 --> D3["3. Read cached descriptor<br/>(missing/corrupt → error; NEVER launch unsandboxed without it)"]
    D3 --> D4["4. Resolve exec (entryId → entries[]; else entries[0]; else Exec[os])"]
    D4 --> D5{"5. sandbox.Enabled?"}
    D5 -->|yes| D5a["SandboxPlanner.Build(spec, UserGrants, appDir, SandboxEnv.FromSystem())"]
    D5 -->|no| D5b["SandboxPlan(Unrestricted: true)"]
    D5a --> D6["6. SandboxLauncherFactory.Current(logger).Launch(...)"]
    D5b --> D6
    D6 --> OS["per-OS backend (see below)"]
```

### Permissions flow (`hina perms` / `verify`)

```mermaid
flowchart TD
    P1["hina perms"] --> T["table of every app's permissions"]
    P2["hina perms &lt;app&gt;"] --> Dt["detail for one app"]
    P3["hina perms &lt;app&gt; --grant &lt;path&gt;[:ro|:rw]"] --> G["add runtime FsGrant (registry lock)"]
    P4["hina perms &lt;app&gt; --revoke &lt;path&gt;"] --> Rv["remove runtime FsGrant (registry lock)"]
    T --- AP["AppPermissions.From(cachedDescriptor, registryRow)<br/>→ PermissionsFormatter<br/>(filesystem enforced; other caps 'declared — not enforced')"]
    Dt --- AP
    V["hina verify [--repair] [--deep]"] --> RV["RegistryVerifier: orphans + local integrity<br/>(--repair prunes; --deep hashes vs manifest)"]
```

---

## Sandbox / container isolation per OS

How a sandboxed app launches and gets isolated. The shell shortcut runs
`hina run <app> <entry>` instead of the binary, so Hina builds the plan and installs
the sandbox **before** the app starts.

### Master launch + isolate flow

```mermaid
flowchart TD
    SC["Shell shortcut / hina run &lt;app&gt; &lt;entry&gt;"] --> DESC["Load cached descriptor"]
    DESC --> EN{"sandbox.Enabled?"}
    EN -->|no| UNR["SandboxPlan(Unrestricted)"]
    EN -->|yes| PLAN["SandboxPlanner.Build"]
    PLAN --> P1["fold: declared FsRules + user FsGrants + always-ro app dir"]
    P1 --> P2["resolve tokens via SandboxEnv / SandboxPaths<br/>(app, home, xdg-documents, xdg-download, xdg-config, tmp, host)"]
    P2 --> P3["host → Unrestricted; duplicate path → rw wins"]
    P3 --> SP["SandboxPlan { Unrestricted, Rules[], RestrictNetwork }"]
    UNR --> SP
    SP --> F{"SandboxLauncherFactory.Current"}
    F -->|"Linux + Landlock supported"| LX["LinuxLandlockSandbox"]
    F -->|macOS| MAC["MacOsSandbox"]
    F -->|"Windows / unsupported / old kernel"| NOP["NoOpSandbox"]
    LX --> LXD["see Linux detail"]
    MAC --> MACD["see macOS detail"]
    NOP --> NOPD["see Windows detail"]
```

### Linux — Landlock (enforced)

Applies the ruleset to `hina` itself, then `execv`s the app, which inherits the
restrictions — the sandboxed app's PID **is** hina's. Any failure warns and execs
unsandboxed; it never blocks a launch.

```mermaid
flowchart TD
    A["LinuxLandlockSandbox.Launch"] --> B{"plan.Unrestricted or ABI unsupported?"}
    B -->|yes| Bx["execv app directly (warn if scope was requested)"]
    B -->|no| C["Probe Landlock ABI"]
    C --> D["Build ruleset: deny every fs access right the ABI knows"]
    D --> E["Grant resolved plan paths + SystemRuntimePaths<br/>(loader/libc/dev nodes so the app can start)"]
    E --> N{"RestrictNetwork and ABI ≥ 4?"}
    N -->|yes| N1["handle TCP bind+connect with no allow rules → denied"]
    N -->|no| N2["network not restricted (logged on old ABI)"]
    N1 --> F["prctl(NO_NEW_PRIVS)"]
    N2 --> F
    F --> G["landlock_restrict_self"]
    G --> H["execv app — inherits ruleset (app PID = hina PID)"]
```

### macOS — Seatbelt / sandbox-exec (enforced)

Generates a Seatbelt profile from the plan and launches the app as a **child** under
`sandbox-exec -f`, so the temp profile can be cleaned up after exit.

```mermaid
flowchart TD
    A["MacOsSandbox.Launch"] --> B{"plan.Unrestricted or no sandbox-exec?"}
    B -->|yes| Bx["spawn app directly"]
    B -->|no| C["Canonicalize plan paths (realpath; /tmp,/var,/etc → /private/...)"]
    C --> D["MacOsSeatbeltProfile.Build → .sb:<br/>import bsd.sb, deny default, exec/fork,<br/>system read baseline, plan rules (ro→read, rw→+write),<br/>network unless RestrictNetwork"]
    D --> E["Write temp .sb profile"]
    E --> F["sandbox-exec -f &lt;profile&gt; &lt;exec&gt; — child process"]
    F --> G["Wait for exit → delete temp profile"]
```

### Windows — NoOp today (AppContainer scaffold, unwired)

The factory returns `NoOpSandbox` on Windows: the app runs unsandboxed with a one-time
warning. The `WindowsSandbox` AppContainer backend exists but is **not selected** — its
lowbox honored no runtime grant on CI (full investigation in
[Windows-Sandbox-Resume.md](Windows-Sandbox-Resume.md)).

```mermaid
flowchart TD
    A["SandboxLauncherFactory on Windows"] --> B["NoOpSandbox.Launch"]
    B --> C["spawn app + wait (warn once if scope was requested)"]
    A -.->|"NOT selected (experimental)"| X["WindowsSandbox.Launch (AppContainer)"]
    X -.->|"dashed = unwired"| X1["CreateAppContainerProfile → container SID"]
    X1 -.-> X2["Grant ACEs (container SID) on app dir + plan rules<br/>+ FILE_TRAVERSE on ancestors"]
    X2 -.-> X3["CreateProcess + SECURITY_CAPABILITIES"]
    X3 -.-> X4["BLOCKED: lowbox denies all granted access on CI<br/>→ kept NoOp until debugged on real Windows"]
```

### Shortcut routing per OS

How the install-time shortcut is wired so launches route through `hina run` (and thus
the sandbox) on the enforcing OSes.

```mermaid
flowchart LR
    O["Sandboxed app installed"] --> L[".desktop (Linux)<br/>Exec = hina run &lt;app&gt; &lt;entry&gt;"]
    O --> M[".app bundle (macOS)<br/>stub script: exec hina run ..."]
    O --> W[".lnk (Windows)<br/>4-arg routing exists, but enforcement OFF<br/>→ points at the binary"]
    L --> LE["Landlock enforced"]
    M --> ME["Seatbelt enforced"]
    W --> WE["NoOp (unsandboxed + warn)"]
```

### Sequence — one sandboxed launch

```mermaid
sequenceDiagram
    actor User
    participant Shell
    participant Run as hina run
    participant Planner as SandboxPlanner
    participant Factory as SandboxLauncherFactory
    participant Backend as ISandboxLauncher
    participant App

    User->>Shell: click shortcut
    Shell->>Run: hina run <app> <entry>
    Run->>Run: load cached descriptor, resolve exec
    Run->>Planner: Build(spec, userGrants, appDir, env)
    Planner-->>Run: SandboxPlan
    Run->>Factory: Current(logger)
    Factory-->>Run: backend for this OS
    Run->>Backend: Launch(execAbs, args, plan, ct)
    Note over Backend: Linux=Landlock+execv · macOS=sandbox-exec child · Windows=NoOp
    Backend->>App: start under isolation
    App-->>User: app window (scoped to granted paths)
```
