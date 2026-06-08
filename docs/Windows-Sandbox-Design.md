# Windows sandbox backend — AppContainer (implemented, NOT verified)

Status: **implemented but unverified — Windows ships as NoOp.** The AppContainer
backend (`Hina.PackageManager/Sandbox/WindowsSandbox.cs`, pure policy in
`WindowsAppContainerPolicy.cs`) is written and its ACL plumbing is proven correct on
CI, but the lowbox it creates honors no runtime access grant, so
`SandboxLauncherFactory` deliberately does **not** select it. On Windows a sandboxed
app runs unsandboxed with a one-time warning, exactly like before. This note records
the design and the investigation so the work can be resumed on a real Windows box.

Why it can't be finished here: the dev host is macOS, so AppContainer cannot be run
or debugged locally — the only feedback channel is the `windows-latest` CI probe
(`scripts/windows-sandbox-probe.ps1`), which gives logs but no interactive debugging
(no Process Explorer, no breakpoints).

## Design: low-privilege AppContainer

`WindowsSandbox : ISandboxLauncher`:

1. **Create / derive an AppContainer profile** (`CreateAppContainerProfile`, or
   `DeriveAppContainerSidFromAppContainerName` if it exists). Stable per-app container
   name `Hina.<appName>` (sanitized, ≤ 64 chars).
2. **Grant explicit ACEs** for the container SID on the app dir + each declared
   `ro`/`rw` path (`GetNamedSecurityInfo` → `SetEntriesInAcl` → `SetNamedSecurityInfo`),
   plus `FILE_TRAVERSE` on each ancestor directory (applied with `SetFileSecurity` so it
   does not re-propagate inheritance and hang on big profile dirs).
3. **Launch** with `CreateProcess` + `STARTUPINFOEX` +
   `PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES` pointing at a `SECURITY_CAPABILITIES`
   built from the container SID. All via AOT-safe `[DllImport]` P/Invoke. `RestrictNetwork`
   maps to omitting the `internetClient` capability SID.
4. **`IsSupported`** = `OperatingSystem.IsWindowsVersionAtLeast(6, 2)`. Fail-soft.

System DLL dirs are deliberately not ACL'd — they already carry an `ALL APPLICATION
PACKAGES` ACE granting read+execute to every container.

## What the CI probe proved

On a real `windows-latest` runner, with `icacls` + `--verbose` logging:

- **The ACL plumbing is correct.** Every grant lands on disk: the container SID gets
  `(RX,W)` on the granted dir (plus an inheritable generic ACE for its children) and
  `(X)` (FILE_TRAVERSE) on each ancestor up to `C:\`, with the existing
  user / Administrators / SYSTEM ACEs preserved.
- **Isolation works.** The container is correctly DENIED an ungranted "secret" dir.
- **But the lowbox honors no runtime grant for actual access.** A granted dir was
  neither readable nor writable, whether granted:
  - the specific package SID, **or** `ALL APPLICATION PACKAGES` (the group the token
    demonstrably has — it runs `cmd.exe` from System32 through it);
  - with the object's integrity label lowered to **Low**, or not;
  - on a deep `%TEMP%` profile path, **or** a shallow `C:\` path.

  Every combination was denied; only the System32 baseline (system-level
  `ALL APPLICATION PACKAGES`) was reachable.

## Leading hypothesis / next step

The pattern — only the system baseline reachable, every runtime grant ignored — points
to an **over-restricted token**: most likely the AppContainer process is running at an
integrity level below the objects (so all write-up is blocked), and/or the lowbox
restricting-SID set isn't being satisfied the way the DACL grants expect. Confirming
this needs **Process Explorer on a real Windows machine** to read the live process's
integrity level and token groups/restricting-SIDs, then adjust token creation
accordingly. Reference implementations that work (e.g. Chromium's AppContainer sandbox)
do access user-profile paths, so this is a fixable wiring issue, not a Windows limit.

Until then, the honest NoOp + install-time warning stays — shipping the backend would
launch apps that cannot read their own install directory, strictly worse than NoOp.

## Verification harness (ready for when it works)

`scripts/windows-sandbox-probe.ps1` + the `windows-sandbox` job in
`.github/workflows/dotnet.yml`: a child runs under the sandbox granted only a `docs:rw`
dir (not a `secret` dir) and must be denied the secret read + allowed the docs write
(`READ=0 WRITE=1`). It SKIP-passes while Windows is NoOp; wire `WindowsSandbox` into the
factory and it becomes the real enforcement proof with no other changes.
