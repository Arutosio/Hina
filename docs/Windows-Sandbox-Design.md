# Windows sandbox backend — design note (deferred)

Status: **not implemented.** On Windows a sandboxed app currently falls back to
`NoOpSandbox` (runs unsandboxed, warns once at install). This note records the
intended design so the work can be picked up without re-deriving it.

Why deferred: the AppContainer path is large and easy to get subtly wrong (ACL
inheritance, profile lifecycle, the app needing read on system DLLs), and it cannot
be verified on the macOS/Linux dev host. Per the project rule "do not ship a
half-enforcing backend that *looks* like it isolates but doesn't", it is better to
keep the honest NoOp + warning than to ship an unverified AppContainer.

## Intended approach: low-privilege AppContainer

Implement `WindowsSandbox : ISandboxLauncher`:

1. **Create / derive an AppContainer profile** for the app
   (`CreateAppContainerProfile`, or `DeriveAppContainerSidFromAppContainerName` if it
   already exists). Use a stable per-app container name (e.g. `Hina.<appName>`).
2. **Grant explicit ACEs** for the container SID on the app dir + each declared
   `ro`/`rw` path and each user grant (`SetEntriesInAcl` / `SetNamedSecurityInfo`),
   plus read+execute on the system DLL directories the app needs to load
   (`C:\Windows\System32`, the app's own dir). Without these the process can't start
   — the Windows analogue of the Linux `SystemRuntimePaths` / macOS system baseline.
3. **Launch** with `CreateProcess` using `STARTUPINFOEX` +
   `PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES` pointing at a
   `SECURITY_CAPABILITIES` built from the container SID (and any capability SIDs).
   All via AOT-safe `[LibraryImport]`/`[DllImport]` P/Invoke — no reflection.
4. **`IsSupported`** = AppContainer APIs available (Windows 8+). Fail-soft: any
   failure logs a warning and runs the app, never blocks the launch.

Network: AppContainer denies network unless the `internetClient` /
`internetClientServer` capability SID is added — so `capabilities.network` maps
naturally (omit the SID to deny, add it to allow), mirroring Landlock/Seatbelt.

## Shortcut routing

Wire `WindowsPlatformIntegration`'s 4-arg `CreateMenuShortcut(launchOverride)` so a
sandboxed app's `.lnk` invokes `hina run <app> <entry>` (like Linux/macOS). Then
flip `InstallService.sandboxEnforceable` to include Windows. Until the backend
exists, leave the `.lnk` pointing at the binary and keep the not-enforced warning.

## Verification (when implemented)

Add a `windows-latest` CI job analogous to `macos-sandbox`: a probe that runs a
child under the AppContainer, asserts an ungranted read is denied and a granted
write allowed, and skip-passes if the AppContainer APIs are unavailable.

## Scope guard

If time-boxed, ship a **correct minimal** version (deny-by-default AppContainer +
app dir + declared paths + system DLL dirs) and clearly log/doc the limitations —
never a backend that appears to isolate but leaks.
