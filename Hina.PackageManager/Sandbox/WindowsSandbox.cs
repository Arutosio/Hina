using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Hina.PackageManager.Sandbox
{
    // Windows filesystem sandbox via AppContainer (lowbox). Wired in by SandboxLauncherFactory.
    //
    // STATUS: verified working on a real Windows 11 desktop (build 26200): the probe
    // (scripts/windows-sandbox-probe.ps1) reports READ=0 WRITE=1 — the container is denied an
    // ungranted secret dir (deny-by-default isolation) yet can write a dir granted the package
    // SID. The earlier "all grants denied" symptom reproduced ONLY on the GitHub windows-latest
    // runner (Windows Server 2025, non-interactive session): AppContainer runtime grants do not
    // take effect in that headless/service context. It is an environment limitation of the CI
    // runner, not a code bug — hence the probe SKIP-passes there and is run for real on a
    // desktop. The pure policy (WindowsAppContainerPolicy) and the ACL plumbing below are correct.
    //
    // Design (for reference): an AppContainer process is denied every securable object unless
    // its DACL grants the per-app AppContainer SID (or a capability SID, or ALL APPLICATION
    // PACKAGES), so it is deny-by-default for free — the Windows analogue of Landlock / Seatbelt.
    //
    // WindowsAppContainerPolicy (pure, unit-tested) decides the container name, the ACEs
    // to grant, and the capability SIDs. This class does the unmanaged plumbing: create /
    // derive the container profile, add the ACEs to each granted path, then CreateProcess
    // under a SECURITY_CAPABILITIES built from the container SID. All Win32 via P/Invoke,
    // no reflection, AOT-safe; the declarations compile on every host but only run on
    // Windows (gated by IsSupported + the OperatingSystem guard in Launch).
    //
    // Fail-soft, like the other backends: any setup failure logs a warning and runs the
    // app directly (unsandboxed) — a sandbox problem never blocks a launch. SandboxLauncherFactory
    // screens true session-0 service contexts via Environment.UserInteractive (NoOp there); note
    // that screen is PARTIAL — some windowed-but-headless contexts (e.g. CI runners) still report
    // UserInteractive == true and reach this class with AppContainer grants silently ignored.
    //
    // System DLL dirs (System32, WinSxS, the GAC) already carry an ALL APPLICATION PACKAGES
    // ACE granting read+execute to every AppContainer, so we deliberately do NOT ACL them
    // (it would need admin and edit a shared system DACL). We grant only the app dir and the
    // plan rules; everything else stays denied.
    public sealed class WindowsSandbox : ISandboxLauncher
    {
        private readonly ILogger _logger;

        public WindowsSandbox(ILogger logger)
        {
            _logger = logger;
        }

        // AppContainer APIs (userenv.dll CreateAppContainerProfile) ship on Windows 8 /
        // Server 2012 and later (NT 6.2+).
        public bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(6, 2);

        public int Launch(string execAbs, IReadOnlyList<string> appArgs, SandboxPlan plan, CancellationToken ct)
        {
            // Guard with the concrete OS check (not the IsSupported property) so the
            // platform-compatibility analyzer can flow it to the AppContainer call below.
            if (plan.Unrestricted || !OperatingSystem.IsWindowsVersionAtLeast(6, 2))
            {
                // Host opt-out or no AppContainer support — run directly.
                if (!plan.Unrestricted && plan.Rules.Count > 1)
                {
                    _logger.LogWarning(
                        "This app requested filesystem sandboxing, but this platform cannot enforce it. Running unsandboxed.");
                }
                return SpawnDirect(execAbs, appArgs, ct);
            }

            try
            {
                return LaunchInAppContainer(execAbs, appArgs, plan, ct);
            }
            catch (Exception ex)
            {
                // Fail-soft: never block a launch because the sandbox couldn't be set up.
                _logger.LogWarning("Could not set up AppContainer sandbox ({Message}); running unsandboxed.", ex.Message);
                return SpawnDirect(execAbs, appArgs, ct);
            }
        }

        private int SpawnDirect(string execAbs, IReadOnlyList<string> appArgs, CancellationToken ct)
        {
            ProcessStartInfo psi = new ProcessStartInfo { FileName = execAbs, UseShellExecute = false };
            foreach (string a in appArgs) psi.ArgumentList.Add(a);

            using Process proc = Process.Start(psi)!;
            using CancellationTokenRegistration reg = ct.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
                catch { /* race: already gone */ }
            });
            proc.WaitForExit();
            return proc.ExitCode;
        }

        // ---- AppContainer launch (Windows-only) ----

        [SupportedOSPlatform("windows")]
        private int LaunchInAppContainer(string execAbs, IReadOnlyList<string> appArgs, SandboxPlan plan, CancellationToken ct)
        {
            string appName   = DeriveAppName(execAbs);
            string anchorPath = DeriveAnchorPath(execAbs, plan);
            string containerName = WindowsAppContainerPolicy.ContainerName(appName, anchorPath);

            IntPtr containerSid = CreateOrDeriveContainerSid(containerName);
            _logger.LogDebug("AppContainer '{Name}' SID {Sid}", containerName, SidToString(containerSid));
            List<IntPtr> capabilitySids = new List<IntPtr>();
            IntPtr capabilitiesArray = IntPtr.Zero;
            IntPtr attributeList = IntPtr.Zero;
            IntPtr secCapsPtr = IntPtr.Zero;
            try
            {
                // 1. Grant the container SID the ACEs the policy computed. The app dir grant
                //    is what lets the container read its own binary — without it the process
                //    can't even start.
                foreach (AppContainerAce ace in WindowsAppContainerPolicy.BuildAceList(plan))
                {
                    GrantContainerAce(ace.Path, containerSid, ace.Access);
                    _logger.LogDebug("AppContainer grant {Access} on {Path}", ace.Access, ace.Path);
                    GrantAncestorTraverse(ace.Path, containerSid);
                }

                // 2. Build the capability SID array (network etc).
                foreach (string sidString in WindowsAppContainerPolicy.BuildCapabilitySids(plan))
                {
                    if (ConvertStringSidToSidW(sidString, out IntPtr capSid) && capSid != IntPtr.Zero)
                    {
                        capabilitySids.Add(capSid);
                    }
                }
                capabilitiesArray = BuildCapabilitiesArray(capabilitySids);

                // 3. SECURITY_CAPABILITIES → process-thread attribute list.
                SECURITY_CAPABILITIES secCaps = new SECURITY_CAPABILITIES
                {
                    AppContainerSid = containerSid,
                    Capabilities = capabilitiesArray,
                    CapabilityCount = (uint)capabilitySids.Count,
                    Reserved = 0,
                };
                secCapsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_CAPABILITIES>());
                Marshal.StructureToPtr(secCaps, secCapsPtr, false);

                attributeList = BuildAttributeList(secCapsPtr);

                // 4. Launch under the container and wait for exit.
                return CreateAndWait(execAbs, appArgs, attributeList, execAbs, ct);
            }
            finally
            {
                if (attributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                }
                if (secCapsPtr != IntPtr.Zero) Marshal.FreeHGlobal(secCapsPtr);
                if (capabilitiesArray != IntPtr.Zero) Marshal.FreeHGlobal(capabilitiesArray);
                foreach (IntPtr capSid in capabilitySids) LocalFree(capSid);
                if (containerSid != IntPtr.Zero) FreeSid(containerSid);
            }
        }

        // Diagnostic only: render a PSID as its S-1-… string for the Debug log trail.
        [SupportedOSPlatform("windows")]
        private static string SidToString(IntPtr sid)
        {
            if (ConvertSidToStringSidW(sid, out IntPtr str) && str != IntPtr.Zero)
            {
                try { return Marshal.PtrToStringUni(str) ?? "?"; }
                finally { LocalFree(str); }
            }
            return "?";
        }

        // Human-readable segment of the container moniker: the executable's file name
        // without extension. Used as the basename part of "Hina.<basename>.<hex8>".
        private static string DeriveAppName(string execAbs)
        {
            string name = Path.GetFileNameWithoutExtension(execAbs);
            return string.IsNullOrEmpty(name) ? "app" : name;
        }

        // Unique anchor path used to hash-differentiate the AppContainer moniker (BUG-009).
        // Two apps with the same exe basename but different install paths must produce
        // distinct monikers so their AppContainer SIDs — and the additive DACL grants
        // written onto each app's directory — never collide.
        //
        // Priority: rules[0].Path (the app's install dir, set by RunCommand / DevCommand)
        //           → directory of execAbs → execAbs itself (last-resort, always non-empty).
        private static string DeriveAnchorPath(string execAbs, SandboxPlan plan)
        {
            if (plan.Rules.Count > 0 && !string.IsNullOrEmpty(plan.Rules[0].Path))
                return plan.Rules[0].Path;

            string? dir = Path.GetDirectoryName(execAbs);
            if (!string.IsNullOrEmpty(dir))
                return dir;

            return execAbs;
        }

        [SupportedOSPlatform("windows")]
        private IntPtr CreateOrDeriveContainerSid(string containerName)
        {
            int hr = CreateAppContainerProfile(containerName, containerName, containerName, IntPtr.Zero, 0, out IntPtr sid);
            if (hr >= 0 && sid != IntPtr.Zero)
            {
                return sid;
            }

            // ERROR_ALREADY_EXISTS as HRESULT — the profile from a previous launch is fine;
            // just derive its (stable) SID.
            const int E_ALREADY_EXISTS = unchecked((int)0x800700B7);
            if (hr == E_ALREADY_EXISTS)
            {
                int dhr = DeriveAppContainerSidFromAppContainerName(containerName, out IntPtr derived);
                if (dhr >= 0 && derived != IntPtr.Zero)
                {
                    return derived;
                }
                throw new InvalidOperationException($"DeriveAppContainerSidFromAppContainerName failed (0x{dhr:X8}).");
            }
            throw new InvalidOperationException($"CreateAppContainerProfile failed (0x{hr:X8}).");
        }

        [SupportedOSPlatform("windows")]
        private void GrantContainerAce(string path, IntPtr containerSid, AppContainerAccess access)
        {
            uint rights = access switch
            {
                AppContainerAccess.ReadWriteExecute => GENERIC_READ | GENERIC_WRITE | GENERIC_EXECUTE,
                _ => GENERIC_READ | GENERIC_EXECUTE,
            };
            // Inheritable so files created in / already under the dir get the right too.
            // propagate=true (SetNamedSecurityInfo) pushes the inheritable ACE to existing
            // children — cheap on a small app/data dir and needed so its files are reachable.
            GrantAce(path, containerSid, rights, SUB_CONTAINERS_AND_OBJECTS_INHERIT, throwOnFail: true, propagate: true);
        }

        // Grant FILE_TRAVERSE on every ancestor directory of a granted leaf so the lowbox
        // token (which does NOT bypass traverse — proven on CI) can walk down to it.
        // Execute-only and NON-inheritable, so the container gains pass-through but cannot
        // enumerate or read the ancestors' contents. propagate=false so each grant uses
        // SetFileSecurity (no subtree re-propagation — SetNamedSecurityInfo on a profile dir
        // walks the whole tree and hangs for minutes). Best-effort: ancestors we can't re-ACL
        // (the drive root, C:\Users) are skipped — they already grant ALL APPLICATION PACKAGES
        // traverse; the profile-private dirs we own are the ones that need it.
        [SupportedOSPlatform("windows")]
        private void GrantAncestorTraverse(string leafPath, IntPtr containerSid)
        {
            DirectoryInfo? dir;
            try { dir = Directory.GetParent(Path.GetFullPath(leafPath)); }
            catch { return; }

            while (dir != null)
            {
                _logger.LogDebug("AppContainer traverse-grant on {Path}", dir.FullName);
                GrantAce(dir.FullName, containerSid, FILE_TRAVERSE, NO_INHERITANCE, throwOnFail: false, propagate: false);
                dir = dir.Parent;
            }
        }

        // Merge one GRANT_ACCESS ACE for the container SID into a path's DACL.
        // throwOnFail=false makes it best-effort (used for the ancestor traverse chain).
        // propagate=false applies the DACL with SetFileSecurity, which does NOT re-propagate
        // inheritance into the subtree (SetNamedSecurityInfo does, and hangs on big dirs).
        [SupportedOSPlatform("windows")]
        private void GrantAce(string path, IntPtr containerSid, uint rights, uint inheritance, bool throwOnFail, bool propagate)
        {
            uint getErr = GetNamedSecurityInfoW(path, SE_FILE_OBJECT, DACL_SECURITY_INFORMATION,
                IntPtr.Zero, IntPtr.Zero, out IntPtr oldDacl, IntPtr.Zero, out IntPtr securityDescriptor);
            if (getErr != ERROR_SUCCESS)
            {
                if (throwOnFail) throw new InvalidOperationException($"GetNamedSecurityInfo('{path}') failed ({getErr}).");
                _logger.LogDebug("AppContainer: GetNamedSecurityInfo('{Path}') failed ({Err}); skipped.", path, getErr);
                return;
            }

            IntPtr newDacl = IntPtr.Zero;
            try
            {
                EXPLICIT_ACCESS_W ea = new EXPLICIT_ACCESS_W
                {
                    grfAccessPermissions = rights,
                    grfAccessMode = GRANT_ACCESS,
                    grfInheritance = inheritance,
                    Trustee = new TRUSTEE_W
                    {
                        pMultipleTrustee = IntPtr.Zero,
                        MultipleTrusteeOperation = NO_MULTIPLE_TRUSTEE,
                        TrusteeForm = TRUSTEE_IS_SID,
                        TrusteeType = TRUSTEE_IS_UNKNOWN,
                        ptstrName = containerSid,
                    },
                };

                uint setErr = SetEntriesInAclW(1, ref ea, oldDacl, out newDacl);
                if (setErr != ERROR_SUCCESS)
                {
                    if (throwOnFail) throw new InvalidOperationException($"SetEntriesInAcl('{path}') failed ({setErr}).");
                    _logger.LogDebug("AppContainer: SetEntriesInAcl('{Path}') failed ({Err}); skipped.", path, setErr);
                    return;
                }

                if (propagate)
                {
                    uint applyErr = SetNamedSecurityInfoW(path, SE_FILE_OBJECT, DACL_SECURITY_INFORMATION,
                        IntPtr.Zero, IntPtr.Zero, newDacl, IntPtr.Zero);
                    if (applyErr != ERROR_SUCCESS)
                    {
                        if (throwOnFail) throw new InvalidOperationException($"SetNamedSecurityInfo('{path}') failed ({applyErr}).");
                        _logger.LogDebug("AppContainer: SetNamedSecurityInfo('{Path}') failed ({Err}); skipped.", path, applyErr);
                    }
                }
                else
                {
                    // Non-propagating: write just this object's DACL. SetFileSecurity does not
                    // walk the subtree, so it stays fast on huge profile directories.
                    IntPtr sd = Marshal.AllocHGlobal(SECURITY_DESCRIPTOR_MIN_LENGTH);
                    try
                    {
                        if (!InitializeSecurityDescriptor(sd, SECURITY_DESCRIPTOR_REVISION)
                            || !SetSecurityDescriptorDacl(sd, true, newDacl, false)
                            || !SetFileSecurityW(path, DACL_SECURITY_INFORMATION, sd))
                        {
                            int err = Marshal.GetLastWin32Error();
                            if (throwOnFail) throw new InvalidOperationException($"SetFileSecurity('{path}') failed ({err}).");
                            _logger.LogDebug("AppContainer: SetFileSecurity('{Path}') failed ({Err}); skipped.", path, err);
                        }
                    }
                    finally { Marshal.FreeHGlobal(sd); }
                }
            }
            finally
            {
                if (newDacl != IntPtr.Zero) LocalFree(newDacl);
                if (securityDescriptor != IntPtr.Zero) LocalFree(securityDescriptor);
            }
        }

        // Allocate an unmanaged SID_AND_ATTRIBUTES[] for the capability SIDs.
        [SupportedOSPlatform("windows")]
        private static IntPtr BuildCapabilitiesArray(List<IntPtr> capabilitySids)
        {
            if (capabilitySids.Count == 0)
            {
                return IntPtr.Zero;
            }

            int size = Marshal.SizeOf<SID_AND_ATTRIBUTES>();
            IntPtr array = Marshal.AllocHGlobal(size * capabilitySids.Count);
            for (int i = 0; i < capabilitySids.Count; i++)
            {
                SID_AND_ATTRIBUTES entry = new SID_AND_ATTRIBUTES
                {
                    Sid = capabilitySids[i],
                    Attributes = SE_GROUP_ENABLED,
                };
                Marshal.StructureToPtr(entry, array + (i * size), false);
            }
            return array;
        }

        [SupportedOSPlatform("windows")]
        private static IntPtr BuildAttributeList(IntPtr secCapsPtr)
        {
            IntPtr size = IntPtr.Zero;
            // First call sizes the buffer; it returns false with ERROR_INSUFFICIENT_BUFFER.
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);

            IntPtr list = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
            {
                Marshal.FreeHGlobal(list);
                throw new InvalidOperationException($"InitializeProcThreadAttributeList failed ({Marshal.GetLastWin32Error()}).");
            }

            if (!UpdateProcThreadAttribute(list, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES,
                    secCapsPtr, (IntPtr)Marshal.SizeOf<SECURITY_CAPABILITIES>(), IntPtr.Zero, IntPtr.Zero))
            {
                DeleteProcThreadAttributeList(list);
                Marshal.FreeHGlobal(list);
                throw new InvalidOperationException($"UpdateProcThreadAttribute failed ({Marshal.GetLastWin32Error()}).");
            }
            return list;
        }

        [SupportedOSPlatform("windows")]
        private int CreateAndWait(string execAbs, IReadOnlyList<string> appArgs, IntPtr attributeList, string applicationName, CancellationToken ct)
        {
            char[] commandLine = BuildCommandLine(execAbs, appArgs);

            STARTUPINFOEX si = new STARTUPINFOEX();
            si.StartupInfo.cb = (uint)Marshal.SizeOf<STARTUPINFOEX>();
            si.lpAttributeList = attributeList;

            string? workingDir = Path.GetDirectoryName(execAbs);

            // bInheritHandles: false — the container launch does NOT redirect stdio, so no
            // handle needs to be inherited by the child. Passing true at a lowbox security
            // boundary without a PROC_THREAD_ATTRIBUTE_HANDLE_LIST is the wrong pattern:
            // any inheritable handle that the parent happens to hold would silently leak
            // into the sandboxed process, widening its ambient authority.
            //
            // If stdio redirection inside the container is ever added, set bInheritHandles
            // back to true AND add PROC_THREAD_ATTRIBUTE_HANDLE_LIST to the attribute list
            // with only the three explicit stdio handles — never an open-ended inherit grant.
            bool ok = CreateProcessW(
                applicationName,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                bInheritHandles: false,
                // CREATE_SUSPENDED so the child can be assigned to a Job Object before it runs,
                // guaranteeing no descendant escapes the kill-on-cancel tree (BUG-048).
                EXTENDED_STARTUPINFO_PRESENT | CREATE_SUSPENDED,
                IntPtr.Zero,
                string.IsNullOrEmpty(workingDir) ? null : workingDir,
                ref si,
                out PROCESS_INFORMATION pi);

            if (!ok)
            {
                throw new InvalidOperationException($"CreateProcess failed ({Marshal.GetLastWin32Error()}).");
            }
            _logger.LogDebug("AppContainer launched pid {Pid} for {Exec}", pi.dwProcessId, execAbs);

            IntPtr hJob = IntPtr.Zero;
            try
            {
                // Confine the child (and every descendant) to a Job Object with
                // KILL_ON_JOB_CLOSE so a cancel terminates the WHOLE tree — parity with the
                // entireProcessTree:true used by the SpawnDirect and macOS backends (BUG-048).
                // The child is suspended; assign it to the job, then resume.
                hJob = CreateJobObject(IntPtr.Zero, null);
                if (hJob != IntPtr.Zero)
                {
                    JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = default;
                    info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                    int len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                    IntPtr buf = Marshal.AllocHGlobal(len);
                    try
                    {
                        Marshal.StructureToPtr(info, buf, false);
                        SetInformationJobObject(hJob, JobObjectExtendedLimitInformation, buf, (uint)len);
                    }
                    finally { Marshal.FreeHGlobal(buf); }

                    if (!AssignProcessToJobObject(hJob, pi.hProcess))
                    {
                        // Rare on a lowbox (e.g. already in an unassignable job). Fall back to
                        // killing only the root, preserving the prior behaviour.
                        _logger.LogDebug("Could not assign pid {Pid} to a job; cancel will kill only the root process.", pi.dwProcessId);
                        CloseHandle(hJob);
                        hJob = IntPtr.Zero;
                    }
                }

                // Resume the suspended child regardless of whether the job was set up.
                ResumeThread(pi.hThread);

                // Poll so a cancellation can terminate the child tree.
                while (true)
                {
                    uint wait = WaitForSingleObject(pi.hProcess, 100);
                    if (wait == WAIT_OBJECT_0) break;
                    if (ct.IsCancellationRequested)
                    {
                        try
                        {
                            if (hJob != IntPtr.Zero) { TerminateJobObject(hJob, 1); }
                            else { TerminateProcess(pi.hProcess, 1); }
                        }
                        catch { /* race */ }
                        WaitForSingleObject(pi.hProcess, INFINITE);
                        break;
                    }
                }
                GetExitCodeProcess(pi.hProcess, out uint exitCode);
                return unchecked((int)exitCode);
            }
            finally
            {
                if (hJob != IntPtr.Zero) { CloseHandle(hJob); }
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
            }
        }

        // CreateProcessW takes one mutable command-line buffer. argv[0] is the exe path;
        // every token is quoted the way CommandLineToArgvW expects.
        // internal so the unit tests in Hina.PackageManager.Tests can exercise it directly.
        internal static char[] BuildCommandLine(string execAbs, IReadOnlyList<string> appArgs)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append(QuoteArg(execAbs));
            foreach (string a in appArgs)
            {
                sb.Append(' ').Append(QuoteArg(a));
            }
            string cmd = sb.ToString();
            char[] buffer = new char[cmd.Length + 1];
            cmd.CopyTo(0, buffer, 0, cmd.Length);
            buffer[cmd.Length] = '\0';
            return buffer;
        }

        // Quote a single argument according to the CommandLineToArgvW rules (the same
        // algorithm used by MSVC's argv parser and Daniel Colascione's ArgvQuote):
        //
        //   • Backslashes are only special immediately before a '"' or at the end of
        //     the quoted string (before the closing '"').
        //   • Before a '"': emit 2*N+1 backslashes, then the escaped '"' (\"').
        //   • At the closing '"': emit 2*N backslashes so the trailing '\' does NOT
        //     escape the closing quote.
        //   • Elsewhere: backslashes are literal — emit them as-is.
        //
        // BUG-020 fix: the previous implementation did not double backslashes before
        // quotes, so e.g. "C:\dir\" was emitted as "C:\dir\" — CommandLineToArgvW
        // then treated \" as an escaped quote, breaking the argument boundary.
        //
        // internal so the unit tests in Hina.PackageManager.Tests can exercise it directly.
        internal static string QuoteArg(string arg)
        {
            // Fast-path: no characters that force quoting (\n and \v also force it per
            // CommandLineToArgvW, so include them here alongside the obvious ones).
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            {
                return arg;
            }

            System.Text.StringBuilder sb = new System.Text.StringBuilder(arg.Length + 4);
            sb.Append('"');

            int i = 0;
            while (i < arg.Length)
            {
                // Count the run of consecutive backslashes.
                int n = 0;
                while (i < arg.Length && arg[i] == '\\') { n++; i++; }

                if (i == arg.Length)
                {
                    // End of string: the backslash run precedes the closing '"'.
                    // Emit 2*N backslashes so none of them escapes the closing quote.
                    sb.Append('\\', n * 2);
                    break;
                }
                else if (arg[i] == '"')
                {
                    // The run precedes a literal '"': emit 2*N+1 backslashes, then \".
                    sb.Append('\\', n * 2 + 1);
                    sb.Append('"');
                    i++;
                }
                else
                {
                    // Ordinary character: backslashes are not special here, emit as-is.
                    sb.Append('\\', n);
                    sb.Append(arg[i]);
                    i++;
                }
            }

            sb.Append('"');
            return sb.ToString();
        }

        // ---- Win32 constants ----

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint GENERIC_EXECUTE = 0x20000000;

        private const int SE_FILE_OBJECT = 1;
        private const uint DACL_SECURITY_INFORMATION = 0x00000004;
        private const uint ERROR_SUCCESS = 0;

        private const int GRANT_ACCESS = 1;
        private const uint SUB_CONTAINERS_AND_OBJECTS_INHERIT = 0x3; // CONTAINER_INHERIT_ACE | OBJECT_INHERIT_ACE
        private const uint NO_INHERITANCE = 0x0;
        private const uint FILE_TRAVERSE = 0x00000020; // execute right on a directory (pass-through, no list)
        private const uint SECURITY_DESCRIPTOR_REVISION = 1;
        private const int SECURITY_DESCRIPTOR_MIN_LENGTH = 40; // absolute SD header on x64
        private const int TRUSTEE_IS_SID = 0;
        private const int TRUSTEE_IS_UNKNOWN = 0;
        private const int NO_MULTIPLE_TRUSTEE = 0;

        private const uint SE_GROUP_ENABLED = 0x00000004;
        private const int PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES = 0x00020009;
        private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        private const uint CREATE_SUSPENDED = 0x00000004;
        private const uint WAIT_OBJECT_0 = 0x00000000;
        private const uint INFINITE = 0xFFFFFFFF;

        // Job Object: kill the whole tree when the job handle closes / is terminated (BUG-048).
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        private const int JobObjectExtendedLimitInformation = 9;

        // ---- Win32 structs ----

        [StructLayout(LayoutKind.Sequential)]
        private struct TRUSTEE_W
        {
            public IntPtr pMultipleTrustee;
            public int MultipleTrusteeOperation;
            public int TrusteeForm;
            public int TrusteeType;
            public IntPtr ptstrName; // PSID when TrusteeForm == TRUSTEE_IS_SID
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EXPLICIT_ACCESS_W
        {
            public uint grfAccessPermissions;
            public int grfAccessMode;
            public uint grfInheritance;
            public TRUSTEE_W Trustee;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SID_AND_ATTRIBUTES
        {
            public IntPtr Sid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_CAPABILITIES
        {
            public IntPtr AppContainerSid;
            public IntPtr Capabilities;
            public uint CapabilityCount;
            public uint Reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFOW
        {
            public uint cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            public IntPtr lpTitle;
            public uint dwX;
            public uint dwY;
            public uint dwXSize;
            public uint dwYSize;
            public uint dwXCountChars;
            public uint dwYCountChars;
            public uint dwFillAttribute;
            public uint dwFlags;
            public ushort wShowWindow;
            public ushort cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFOEX
        {
            public STARTUPINFOW StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        // ---- Win32 P/Invoke (AOT-safe: explicit marshalling, no reflection) ----

        [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
        private static extern int CreateAppContainerProfile(string pszAppContainerName, string pszDisplayName,
            string pszDescription, IntPtr pCapabilities, uint dwCapabilityCount, out IntPtr ppSidAppContainerSid);

        [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
        private static extern int DeriveAppContainerSidFromAppContainerName(string pszAppContainerName, out IntPtr ppSidAppContainerSid);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ConvertStringSidToSidW(string StringSid, out IntPtr Sid);

        [DllImport("advapi32.dll")]
        private static extern IntPtr FreeSid(IntPtr pSid);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ConvertSidToStringSidW(IntPtr Sid, out IntPtr StringSid);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetNamedSecurityInfoW(string pObjectName, int ObjectType, uint SecurityInfo,
            IntPtr ppsidOwner, IntPtr ppsidGroup, out IntPtr ppDacl, IntPtr ppSacl, out IntPtr ppSecurityDescriptor);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint SetNamedSecurityInfoW(string pObjectName, int ObjectType, uint SecurityInfo,
            IntPtr psidOwner, IntPtr psidGroup, IntPtr pDacl, IntPtr pSacl);

        // Legacy by-name DACL setter: unlike SetNamedSecurityInfo it does NOT re-propagate
        // inheritance into the subtree, so it stays fast on large directories.
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileSecurityW(string lpFileName, uint SecurityInformation, IntPtr pSecurityDescriptor);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InitializeSecurityDescriptor(IntPtr pSecurityDescriptor, uint dwRevision);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetSecurityDescriptorDacl(IntPtr pSecurityDescriptor,
            [MarshalAs(UnmanagedType.Bool)] bool bDaclPresent, IntPtr pDacl, [MarshalAs(UnmanagedType.Bool)] bool bDaclDefaulted);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint SetEntriesInAclW(uint cCountOfExplicitEntries, ref EXPLICIT_ACCESS_W pListOfExplicitEntries,
            IntPtr OldAcl, out IntPtr NewAcl);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount,
            int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute,
            IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcessW(string? lpApplicationName, char[]? lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
            uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInformationClass,
            IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
