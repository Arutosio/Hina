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
    // Windows filesystem (+ network) sandbox backend, built on AppContainer. An
    // AppContainer process is denied every securable object unless its DACL grants the
    // per-app AppContainer SID (or a capability SID, or ALL APPLICATION PACKAGES), so it
    // is deny-by-default for free — the Windows analogue of Landlock / Seatbelt.
    //
    // WindowsAppContainerPolicy (pure, unit-tested) decides the container name, the ACEs
    // to grant, and the capability SIDs. This class does the unmanaged plumbing: create /
    // derive the container profile, add the ACEs to each granted path, then CreateProcess
    // under a SECURITY_CAPABILITIES built from the container SID. All Win32 via P/Invoke,
    // no reflection, AOT-safe; the declarations compile on every host but only run on
    // Windows (gated by IsSupported + the OperatingSystem guard in Launch).
    //
    // Fail-soft, like the other backends: any setup failure logs a warning and runs the
    // app directly (unsandboxed) — a sandbox problem never blocks a launch. Real isolation
    // is proven by the windows-latest CI probe, which cannot run on the dev host.
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
            string containerName = WindowsAppContainerPolicy.ContainerName(DeriveAppName(execAbs));

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

        // App name used for the container moniker: the executable's file name without
        // extension. Stable per installed app (each app's exec lives in its own dir).
        private static string DeriveAppName(string execAbs)
        {
            string name = Path.GetFileNameWithoutExtension(execAbs);
            return string.IsNullOrEmpty(name) ? "app" : name;
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

            uint getErr = GetNamedSecurityInfoW(path, SE_FILE_OBJECT, DACL_SECURITY_INFORMATION,
                IntPtr.Zero, IntPtr.Zero, out IntPtr oldDacl, IntPtr.Zero, out IntPtr securityDescriptor);
            if (getErr != ERROR_SUCCESS)
            {
                throw new InvalidOperationException($"GetNamedSecurityInfo('{path}') failed ({getErr}).");
            }

            IntPtr newDacl = IntPtr.Zero;
            try
            {
                EXPLICIT_ACCESS_W ea = new EXPLICIT_ACCESS_W
                {
                    grfAccessPermissions = rights,
                    grfAccessMode = GRANT_ACCESS,
                    grfInheritance = SUB_CONTAINERS_AND_OBJECTS_INHERIT,
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
                    throw new InvalidOperationException($"SetEntriesInAcl('{path}') failed ({setErr}).");
                }

                uint applyErr = SetNamedSecurityInfoW(path, SE_FILE_OBJECT, DACL_SECURITY_INFORMATION,
                    IntPtr.Zero, IntPtr.Zero, newDacl, IntPtr.Zero);
                if (applyErr != ERROR_SUCCESS)
                {
                    throw new InvalidOperationException($"SetNamedSecurityInfo('{path}') failed ({applyErr}).");
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

            bool ok = CreateProcessW(
                applicationName,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                bInheritHandles: true,
                EXTENDED_STARTUPINFO_PRESENT,
                IntPtr.Zero,
                string.IsNullOrEmpty(workingDir) ? null : workingDir,
                ref si,
                out PROCESS_INFORMATION pi);

            if (!ok)
            {
                throw new InvalidOperationException($"CreateProcess failed ({Marshal.GetLastWin32Error()}).");
            }
            _logger.LogDebug("AppContainer launched pid {Pid} for {Exec}", pi.dwProcessId, execAbs);

            try
            {
                // Poll so a cancellation can terminate the child (mirrors the kill-on-cancel
                // behaviour of the other backends).
                while (true)
                {
                    uint wait = WaitForSingleObject(pi.hProcess, 100);
                    if (wait == WAIT_OBJECT_0) break;
                    if (ct.IsCancellationRequested)
                    {
                        try { TerminateProcess(pi.hProcess, 1); } catch { /* race */ }
                        WaitForSingleObject(pi.hProcess, INFINITE);
                        break;
                    }
                }
                GetExitCodeProcess(pi.hProcess, out uint exitCode);
                return unchecked((int)exitCode);
            }
            finally
            {
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
            }
        }

        // CreateProcessW takes one mutable command-line buffer. argv[0] is the exe path;
        // every token is quoted the way CommandLineToArgvW expects.
        private static char[] BuildCommandLine(string execAbs, IReadOnlyList<string> appArgs)
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

        private static string QuoteArg(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            {
                return arg;
            }
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
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
        private const int TRUSTEE_IS_SID = 0;
        private const int TRUSTEE_IS_UNKNOWN = 0;
        private const int NO_MULTIPLE_TRUSTEE = 0;

        private const uint SE_GROUP_ENABLED = 0x00000004;
        private const int PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES = 0x00020009;
        private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        private const uint WAIT_OBJECT_0 = 0x00000000;
        private const uint INFINITE = 0xFFFFFFFF;

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
    }
}
