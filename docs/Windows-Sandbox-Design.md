# Windows sandbox backend — AppContainer (implemented and verified)

Status: **implemented, verified, and wired in.** The AppContainer backend
(`Hina.PackageManager/Sandbox/WindowsSandbox.cs`, pure policy in
`WindowsAppContainerPolicy.cs`) is selected by `SandboxLauncherFactory` on Windows 8+.
On a real Windows 11 desktop (build 26200) the probe reports `READ=0 WRITE=1` — an
ungranted "secret" dir is denied while a dir granted the container's package SID is
writable.

The earlier "honors no runtime grant" symptom (below) was a **CI-environment artifact**,
not a code bug: the GitHub `windows-latest` runner (Windows Server 2025, non-interactive
service session) cannot honour AppContainer runtime grants. On such a session Hina fails
soft to a direct launch with a warning, and the probe SKIP-passes; on a real desktop it
PASSes. Originally this could not be diagnosed because the dev host was macOS — the only
feedback was the headless CI probe. Diagnosed 2026-06-13 on a real Windows box: a sandboxed
`whoami` confirmed a Low-integrity lowbox token, and the probe confirmed grants are honoured.

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

## Historical: what the CI probe showed (now understood as a CI-runner limitation)

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

## Resolution

Diagnosed on a real Windows 11 desktop. A sandboxed `whoami /all` showed the child at
**Low integrity** with a lowbox token; the probe then reported `READ=0 WRITE=1` — the
ungranted secret denied (deny-by-default isolation active) and the package-SID-granted
dir writable (the grant is honoured). So the code was correct all along; the CI runner's
non-interactive/service session is simply a context where AppContainer runtime grants do
not apply. The backend is now selected by the factory and Windows is included in
`sandboxEnforceable`, so sandboxed apps route through `hina run`.

## Verification harness

`scripts/windows-sandbox-probe.ps1` + the `windows-sandbox` job in
`.github/workflows/dotnet.yml`: a child runs under the sandbox granted only a `docs:rw`
dir (not a `secret` dir) and must be denied the secret read + allowed the docs write
(`READ=0 WRITE=1`). On the CI runner (which can't honour AppContainer) hina fails soft and
the probe SKIP-passes; on a real desktop session it runs the full enforcement check and
PASSes. To reproduce locally: build `Hina.CLI` and run
`./scripts/windows-sandbox-probe.ps1 -HinaExe <path-to-Hina.CLI.exe>`.
