# Windows Sandbox — Resume / Handoff (read this first)

> **Purpose of this file.** Everything attempted on the Windows AppContainer sandbox,
> why it is not finished, exactly what was proven, and exactly how to resume — written
> to be exhaustive so a fresh Claude Code session (or a human) can pick it up with
> minimal doubt. **Tell the new session: "read `docs/Windows-Sandbox-Resume.md`".**
>
> **RESOLVED (2026-06-13).** Diagnosed on a real Windows 11 desktop (build 26200): the
> backend works — the probe reports `READ=0 WRITE=1` (ungranted secret denied, package-SID
> grant honoured). The earlier "all grants denied" was a **CI-environment artifact**: the
> GitHub `windows-latest` runner (Windows Server 2025, non-interactive/service session)
> cannot honour AppContainer runtime grants. Not a code bug. The backend is now wired in
> (`SandboxLauncherFactory` + `sandboxEnforceable |= Windows`); the probe SKIP-passes on CI
> and PASSes on a desktop. The historical investigation below is kept for reference.
>
> One-line status (historical): the AppContainer backend is fully implemented and its ACL
> plumbing is proven correct on real Windows CI, but the lowbox process it creates is denied
> all runtime-granted access on the CI runner.

---

## 1. Context — what the sandbox feature is

Hina is a cross-platform NativeAOT desktop-app package manager (C# / .NET 10) at
`/Users/arutosio/GitRepositories/Hina`. The sandbox feature gives Hina-installed apps
Flatpak-style **filesystem (+ network) isolation**, enforced before the app starts.

- **Architecture:** sandboxed apps don't launch their binary directly. Their shortcut
  routes through `hina run <app> <entry>` (the "launchOverride"), which resolves the
  declared scope + user grants into an OS backend ruleset and applies it, then launches
  the app. The chokepoint is `RunCommand.cs`; the cross-OS input is `SandboxPlan`.
- **Working backends:**
  - **Linux** → Landlock (`LinuxLandlockSandbox.cs`), proven on Ubuntu CI.
  - **macOS** → `sandbox-exec`/Seatbelt (`MacOsSandbox.cs` + `MacOsSeatbeltProfile.cs`),
    proven locally + macOS CI.
- **Windows** → was the only OS with no backend (NoOp + warn). This branch tried to add
  an **AppContainer** backend. It did not reach a provable working state.

Key interface: `ISandboxLauncher { bool IsSupported; int Launch(execAbs, appArgs, plan, ct); }`.
`SandboxLauncherFactory.Current(logger)` selects the backend per OS.
`SandboxPlan { bool Unrestricted; IReadOnlyList<ResolvedFsRule> Rules; bool RestrictNetwork }`,
`ResolvedFsRule { string Path; bool CanWrite }`. `plan.Rules[0]` is conventionally the app dir.

**Hard constraint:** the dev host is **macOS**. AppContainer cannot be run or debugged
locally. The ONLY feedback is the `windows-latest` GitHub Actions CI probe — logs only,
no interactive debugging (no Process Explorer, no breakpoints). Every theory below was
tested by pushing a commit and reading CI logs (~4 min/cycle, ~10 cycles total).

---

## 2. Files (all on branch `feature/sandbox-windows`, PR #8)

| File | What it is | Keep? |
|------|-----------|-------|
| `Hina.PackageManager/Sandbox/WindowsAppContainerPolicy.cs` | **Pure** policy: container moniker, the `(path, AppContainerAccess)` ACE list, capability SIDs. 9 host-runnable unit tests. **Correct.** | ✅ |
| `Hina.PackageManager.Tests/WindowsAppContainerPolicyTests.cs` | Tests for the above. Pass. | ✅ |
| `Hina.PackageManager/Sandbox/WindowsSandbox.cs` | The AppContainer backend (`ISandboxLauncher`) — all Win32 via `[DllImport]`. **EXPERIMENTAL, NOT wired into the factory.** Header documents status. | ✅ scaffold |
| `Hina.PackageManager/Platform/Windows/WindowsLaunchOverride.cs` | **Pure** parser splitting `"exe" args` into `.lnk` target+args. Tested. | ✅ |
| `Hina.PackageManager.Tests/WindowsLaunchOverrideTests.cs` | Tests for the parser. Pass. | ✅ |
| `Hina.PackageManager/Platform/Windows/ShellLink.cs` | Gained a `SetArguments` call (4-arg `.lnk`). | ✅ |
| `Hina.PackageManager/Platform/Windows/WindowsPlatformIntegration.cs` | 4-arg `CreateMenuShortcut` routing through `hina run` (dormant on Windows while enforcement is off). | ✅ |
| `scripts/windows-sandbox-probe.ps1` | The CI probe. Currently SKIP-passes under NoOp; becomes the real enforcement check if the backend is wired in. | ✅ |
| `.github/workflows/dotnet.yml` | New `windows-sandbox` job (mirrors `macos-sandbox`). | ✅ |

**Wiring switches (currently set to OFF / honest):**
- `SandboxLauncherFactory.cs` — the `else if (...Windows)` branch is **removed**; Windows
  falls through to `NoOpSandbox`. **To re-enable the backend, restore that branch.**
- `InstallService.cs` (~line 186) — `sandboxEnforceable = Linux || OSX` (Windows removed).
  **Add `|| ...Windows` back once the backend works** so shortcuts route through `hina run`
  and the install disclosure says "runs sandboxed".

---

## 3. How the AppContainer backend works (the design)

`WindowsSandbox.LaunchInAppContainer` (only runs on Windows ≥ 6.2; fail-soft to a direct
spawn on any error):

1. **Create / derive the profile.** `CreateAppContainerProfile("Hina.<app>", …, out sid)`;
   on `ERROR_ALREADY_EXISTS` (0x800700B7) → `DeriveAppContainerSidFromAppContainerName`.
   Returns the **package SID** (`S-1-15-2-…`).
2. **Grant ACEs** (`WindowsAppContainerPolicy.BuildAceList` → `GrantContainerAce`): for each
   plan rule, `GetNamedSecurityInfo` → `SetEntriesInAcl` (merge an `ACCESS_ALLOWED` ACE for
   the container SID) → `SetNamedSecurityInfo`. `ro`→`GENERIC_READ|EXECUTE`, `rw`→`+WRITE`,
   inheritable (`SUB_CONTAINERS_AND_OBJECTS_INHERIT`).
3. **Grant ancestor traverse** (`GrantAncestorTraverse`): the lowbox does NOT bypass traverse,
   so it needs `FILE_TRAVERSE` (execute only, non-inheritable → no list/read) on every parent
   dir up to the drive root. Applied with **`SetFileSecurity`** (NOT `SetNamedSecurityInfo`)
   because the latter re-propagates inheritance over the whole subtree and **hangs for minutes**
   on profile dirs (learned the hard way — see §4).
4. **Build `SECURITY_CAPABILITIES`** { AppContainerSid, Capabilities[], count } and attach it
   via `InitializeProcThreadAttributeList` + `UpdateProcThreadAttribute(PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES=0x00020009)`.
   Capabilities = `internetClient` (`S-1-15-3-1`) unless `RestrictNetwork`.
5. **`CreateProcessW`** with `EXTENDED_STARTUPINFO_PRESENT` + `STARTUPINFOEX`, wait, return exit code.

System DLL dirs (System32, WinSxS, GAC) are deliberately **not** ACL'd — they already carry an
`ALL APPLICATION PACKAGES` (`S-1-15-2-1`) ACE granting read+execute to every container.

---

## 4. The investigation — every step, in order

The probe (`scripts/windows-sandbox-probe.ps1`) runs a child `cmd.exe` under the sandbox,
granting only a `docs:rw` dir (NOT a `secret` dir), and measures booleans via an inline
`cmd /v:on /c "…"`. `--verbose` makes hina log the SID + each grant + the launched pid;
the probe also dumps `icacls` of the relevant dirs.

### 4.1 Bring-up bugs (fixed)
- Probe `cmd.exe` couldn't read a `.cmd` from the app dir → **switched to an inline command**
  (like the Landlock probe's `sh -c`), removing the app-dir-file-read confound.
- `--verbose` placed as `args[0]` → the CLI router printed help (it dispatches on the first
  arg). **Fixed: `--verbose` goes after the `sandbox-run` verb.**
- First Windows CI run exposed **5 pre-existing Unix-assuming tests** that had never run on
  Windows (no prior Windows job): `NoOpSandboxTests` (×2, `/bin/sh`), `LinuxPlatformIntegrationTests`
  / `LinuxSandboxShortcutTests` (Unix path asserts), and a real test bug in
  `WindowsPlatformIntegrationTests.InstallFont` (split on `|` but code uses `\x1F`). All gated/fixed.

### 4.2 The `SetNamedSecurityInfo` hang (fixed)
First ancestor-traverse attempt used `SetNamedSecurityInfo` on each ancestor → it re-propagates
inheritance over the **entire subtree**, so calling it on `C:\Users\runneradmin` walked the whole
profile and **hung CI for 10+ min**. Replaced with `SetFileSecurity` (sets just that object's DACL,
no subtree walk). Job time dropped to ~2 min.

### 4.3 The core finding — measurements

Each row is one CI cycle. `RSEC` = read ungranted secret (want 0). `RDOC`/`WPKG` = read/write a
package-SID-granted dir (want 1). `WAAP` = write an `ALL APPLICATION PACKAGES`-granted dir. `WLOW`
= write a package-SID dir with the object's integrity label lowered to Low. `WROOT`/`WAR`/`WARLOW`
= the same but on a **shallow `C:\` dir** (short, system-traversable path).

| Test | Result | Conclusion |
|------|--------|-----------|
| Baseline (deep profile, package SID) | `RSEC=0 RDOC=0 WDOC=0` | isolation works; granted dir totally unreachable |
| + ancestor `FILE_TRAVERSE` grants (deep) | `RSEC=0 RDOC=0 WDOC=0` | **icacls confirms the `(X)` traverse ACE landed on every ancestor and `(RX,W)` on docs, user/Admin/SYSTEM ACEs preserved** — yet still denied |
| package SID vs `ALL APP PACKAGES` vs Low integrity (deep) | `RSEC=0 WPKG=0 WAAP=0 WLOW=0` | none help — not a SID-match nor an integrity issue |
| shallow `C:\` dir (package SID) | `RSEC=0 WPKG=0 WROOT=0` | not a profile-path / runner-ACL issue |
| shallow `C:\` dir, `ALL APP PACKAGES`, ±Low integrity | `RSEC=0 WPKG=0 WAR=0 WARLOW=0` | **even the group the token provably has, on a traversable C:\ path, is denied** |

**`icacls` evidence (representative), proving the grants are correct on disk:**
```
docs   S-1-15-2-<pkg>:(RX,W)
       S-1-15-2-<pkg>:(OI)(CI)(IO)(GR,GW,GE)
       NT AUTHORITY\SYSTEM:(I)(OI)(CI)(F)
       BUILTIN\Administrators:(I)(OI)(CI)(F)
       runnervm…\runneradmin:(I)(OI)(CI)(F)
<work> S-1-15-2-<pkg>:(X)         <- ancestor traverse landed
<temp> S-1-15-2-<pkg>:(X)
rootA  APPLICATION PACKAGE AUTHORITY\ALL APPLICATION PACKAGES:(OI)(CI)(M)   <- still denied
```

### 4.4 What is therefore PROVEN
1. The lowbox **is** active and isolating — it correctly denies the ungranted secret.
2. The token **has** the `ALL APPLICATION PACKAGES` group — it launches `cmd.exe` from System32,
   which is granted only to that group.
3. Every ACL grant Hina makes **lands correctly** on disk (verified by `icacls`), with the
   pre-existing user/Admin/SYSTEM ACEs intact (so the dual-principal **intersection** model has
   both principals granted).
4. **Yet the lowbox honors no runtime grant** for read or write — package SID, ALL APP PACKAGES,
   ±Low integrity, deep or shallow path: all denied. Only the system baseline (System32) is reachable.

### 4.5 Hypotheses tested and RULED OUT
- ❌ Grant code broken → no (`icacls` shows the ACEs).
- ❌ User/group principal missing (dual-principal intersection) → no (user ACEs preserved).
- ❌ Ancestor traversal → granted `(X)` on the full chain, still denied.
- ❌ Profile-path / runner ACLs → a shallow `C:\` dir behaves identically.
- ❌ Specific package SID not matching → `ALL APPLICATION PACKAGES` (definitely in the token) also denied.
- ❌ Mandatory Integrity (write-up) → lowering the object label to Low did not help, and **reads** fail
  too (read-up isn't integrity-blocked by default).

### 4.6 Leading hypothesis (UNCONFIRMED — needs a real Windows box)
An **over-restricted token**. Most likely the AppContainer process runs at an integrity level
**below** the objects (blocking all write-up) AND/OR the lowbox **restricting-SID set** isn't being
satisfied the way plain DACL grants expect (a lowbox does a second, "restricted" access check using
only the package SID + capabilities; if our `SECURITY_CAPABILITIES` / token wiring is subtly wrong,
that second check denies everything outside the system baseline). Reference sandboxes that work
(e.g. **Chromium's** AppContainer renderer) *do* access user-profile paths, so this is a **fixable
wiring bug, not a Windows limitation.**

---

## 5. How to resume (next week, on a real Windows machine)

### 5.1 Reproduce locally
```powershell
git fetch; git checkout feature/sandbox-windows
dotnet build Hina.sln -c Release
$hina = (Get-ChildItem Hina.CLI/bin/Release -Recurse -Filter Hina.CLI.exe | Select-Object -First 1).FullName
# Temporarily re-enable the backend (see §2 wiring switches) THEN rebuild before this:
./scripts/windows-sandbox-probe.ps1 -HinaExe $hina
```
(The probe currently SKIP-passes because the factory returns NoOp. To exercise the real backend,
first restore the `Windows → WindowsSandbox` branch in `SandboxLauncherFactory.cs` and rebuild.)

### 5.2 Diagnose the token (the key missing data)
Launch a sandboxed `cmd.exe` that **stays alive** (e.g. `cmd /k` or a sleeping child), then with
**Process Explorer** (Sysinternals) inspect that child process:
- **Security tab → Integrity level.** If it's below Medium/Low, that explains the universal write
  denial (write-up). Compare to a known-good AppContainer app.
- **Token groups & "restricting SIDs".** Confirm the package SID + `ALL APPLICATION PACKAGES` are in
  BOTH the normal and the restricting sets. If the restricting set is wrong/empty, the second access
  check denies everything → matches the symptom.
- Cross-check with `whoami /groups` / `whoami /all` run *inside* the container.
- Also useful: Sysinternals **AccessChk** — `accesschk.exe -<pkgSID> C:\path\docs` to see the
  effective access the kernel computes for that SID on the granted dir.

### 5.3 Likely fixes to try (once the cause is confirmed)
- If integrity is the issue: ensure the process isn't created below the object IL; or set granted
  objects' mandatory label appropriately. (Note: the *real* Hina install dir is `%LOCALAPPDATA%\Hina\Apps\<app>`.)
- If the restricting-SID/capabilities wiring is the issue: re-check the `SECURITY_CAPABILITIES`
  struct marshalling, the `SID_AND_ATTRIBUTES` array (`SE_GROUP_ENABLED=0x4`), and that the
  attribute value `0x00020009` + sizes are correct. Compare byte-for-byte against a known-good C++
  sample (e.g. `MalwareTech/AppContainerSandbox` `ContainerCreate.cpp`, or Pavel Yosifovich's
  "Fun with AppContainers").
- The pure policy (`WindowsAppContainerPolicy`) is correct; the bug is in `WindowsSandbox`'s token
  creation, not the policy.

### 5.4 When it works
1. Restore the factory branch + `sandboxEnforceable |= Windows` (§2).
2. The probe asserts `READ=0 WRITE=1` on real Windows → it will PASS (not SKIP) → that is the proof.
3. Update the docs (Security / PackageManager-Guide / Windows-Sandbox-Design) back to "Windows enforces".
4. Remove the "EXPERIMENTAL — NOT WIRED IN" header from `WindowsSandbox.cs`.

---

## 6. Reference

- **Branch:** `feature/sandbox-windows` → **PR #8**. 14 commits (`84a2264` first … `7fb54cc` last).
- **CI:** `.github/workflows/dotnet.yml` job `windows-sandbox` on `windows-latest` (Windows Server 2025).
  Read logs with `gh run view --job <id> --log`.
- **External docs consulted:** Microsoft "Implementing an AppContainer" (Win32 SecAuthZ); Pavel
  Yosifovich "Fun with AppContainers"; Project Zero "Understanding Network Access in Windows
  AppContainers"; `MalwareTech/AppContainerSandbox`.
- **Companion design note:** `docs/Windows-Sandbox-Design.md` (shorter summary of the same).
- Linux/macOS backends are unaffected and fully working; this is Windows-only.
