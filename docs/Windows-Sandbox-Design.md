# Windows sandbox backend — AppContainer (implemented)

Status: **implemented.** On Windows a sandboxed app runs inside a low-privilege
AppContainer, the deny-by-default analogue of Linux/Landlock and macOS/Seatbelt.
Backend: `Hina.PackageManager/Sandbox/WindowsSandbox.cs`; pure policy:
`WindowsAppContainerPolicy.cs`. Proven by the `windows-sandbox` CI job
(`scripts/windows-sandbox-probe.ps1`), since AppContainer cannot be exercised on
the macOS/Linux dev host.

## How it works: low-privilege AppContainer

`WindowsSandbox : ISandboxLauncher`:

1. **Create / derive an AppContainer profile** for the app
   (`CreateAppContainerProfile`, or `DeriveAppContainerSidFromAppContainerName` when
   the profile already exists). Stable per-app container name `Hina.<appName>`
   (sanitized, ≤ 64 chars) so re-launches derive the same SID.
2. **Grant explicit ACEs** for the container SID on the app dir + each declared
   `ro`/`rw` path and user grant (`GetNamedSecurityInfo` → `SetEntriesInAcl` →
   `SetNamedSecurityInfo`). `ro` → read+execute, `rw` → +write. The ACEs are
   inheritable (`SUB_CONTAINERS_AND_OBJECTS_INHERIT`).
3. **Launch** with `CreateProcess` + `STARTUPINFOEX` +
   `PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES` pointing at a
   `SECURITY_CAPABILITIES` built from the container SID (plus any capability SIDs).
   All via AOT-safe `[DllImport]` P/Invoke — no reflection. Wait, return the exit
   code, free the SIDs / attribute list / capability array.
4. **`IsSupported`** = `OperatingSystem.IsWindowsVersionAtLeast(6, 2)` (Windows 8 /
   Server 2012+). Fail-soft: any setup failure logs a warning and runs the app
   directly, never blocking the launch. The factory hands back NoOp when unsupported.

### Why the system DLL dirs are NOT ACL'd

An AppContainer is denied every securable object unless its DACL grants the
container SID, a capability SID, or `ALL APPLICATION PACKAGES` (S-1-15-2-1).
`C:\Windows\System32`, `WinSxS`, and the .NET GAC already carry an
`ALL APPLICATION PACKAGES` read+execute ACE — that is how Store apps load system
DLLs — so the container can load them without any change from Hina. Adding our own
ACEs there would need admin and would edit a **shared system DACL**, so we
deliberately don't: only the app dir and the plan rules get container-SID ACEs.

### Network

An AppContainer denies network unless the `internetClient` capability SID
(`S-1-15-3-1`) is attached. `WindowsAppContainerPolicy.BuildCapabilitySids` maps
`plan.RestrictNetwork`: omit the SID to deny, add it to allow — mirroring
Landlock/Seatbelt.

## Shortcut routing

`WindowsPlatformIntegration`'s 4-arg `CreateMenuShortcut(launchOverride)` writes a
`.lnk` whose target is the hina executable with `run <app> "<entry>"` as its
arguments (the opaque override string is split by `WindowsLaunchOverride.Parse` and
control-stripped), so a sandboxed app's shortcut routes through `hina run` and the
AppContainer is installed before the app starts — like Linux/macOS.
`InstallService.sandboxEnforceable` includes Windows, so the install-time
disclosure says the app runs sandboxed.

## Verification

The `windows-sandbox` job in `.github/workflows/dotnet.yml` runs
`scripts/windows-sandbox-probe.ps1`: a child runs under an AppContainer granted only
a `docs:rw` dir (not a `secret` dir) and must be **denied** the secret read while
**allowed** the docs write (`READ=0 WRITE=1`). It skip-passes where AppContainer is
unavailable. Same contract as `landlock-probe.sh` / the macOS sandbox test.

## Known limitations

- The container profile and the path ACEs are left in place after exit (the
  container name / SID is stable per app, so re-grants are idempotent and the ACE
  only ever benefits that one Hina-managed container). Full teardown on uninstall is
  future work.
- `rw` grants read+write+execute but not `DELETE`; apps that must delete files in a
  granted dir may need a wider right (future work).
