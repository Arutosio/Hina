using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Hina.PackageManager.Sandbox
{
    // Linux filesystem sandbox using Landlock (kernel 5.13+). Unprivileged, no
    // root/bubblewrap. Strategy: build a ruleset that "handles" (i.e. denies by
    // default) every filesystem access right the running ABI knows, then grant
    // back only the resolved paths. Restrictions are applied to THIS process via
    // landlock_restrict_self and then inherited across execv — so `hina run`
    // replaces its own image with the sandboxed app (the app's PID is hina's).
    //
    // Enforcement is Linux-only; the P/Invoke declarations compile everywhere but
    // are never called off Linux (the factory hands back NoOpSandbox instead).
    public sealed class LinuxLandlockSandbox : ISandboxLauncher
    {
        // syscall numbers — identical on x86_64 and aarch64 (added together in 5.13).
        private const long SYS_landlock_create_ruleset = 444;
        private const long SYS_landlock_add_rule = 445;
        private const long SYS_landlock_restrict_self = 446;

        private const uint LANDLOCK_CREATE_RULESET_VERSION = 1u << 0;
        private const int LANDLOCK_RULE_PATH_BENEATH = 1;

        private const int PR_SET_NO_NEW_PRIVS = 38;
        private const int O_PATH = 0x200000;
        private const int O_CLOEXEC = 0x80000;

        // Access-right bits (ABI v1 unless noted).
        private const ulong FS_EXECUTE = 1ul << 0;
        private const ulong FS_WRITE_FILE = 1ul << 1;
        private const ulong FS_READ_FILE = 1ul << 2;
        private const ulong FS_READ_DIR = 1ul << 3;
        private const ulong FS_REMOVE_DIR = 1ul << 4;
        private const ulong FS_REMOVE_FILE = 1ul << 5;
        private const ulong FS_MAKE_CHAR = 1ul << 6;
        private const ulong FS_MAKE_DIR = 1ul << 7;
        private const ulong FS_MAKE_REG = 1ul << 8;
        private const ulong FS_MAKE_SOCK = 1ul << 9;
        private const ulong FS_MAKE_FIFO = 1ul << 10;
        private const ulong FS_MAKE_BLOCK = 1ul << 11;
        private const ulong FS_MAKE_SYM = 1ul << 12;
        private const ulong FS_REFER = 1ul << 13;      // ABI v2
        private const ulong FS_TRUNCATE = 1ul << 14;   // ABI v3

        // Network access-right bits (ABI v4 / kernel 6.7+). Scoping TCP bind/connect
        // lets us actually ENFORCE the declared `network` capability.
        private const int NET_ABI = 4;
        private const ulong NET_BIND_TCP = 1ul << 0;
        private const ulong NET_CONNECT_TCP = 1ul << 1;

        // Implicit system-runtime grants. A dynamically-linked Linux app must read
        // the loader (/lib64/ld-linux-*), libc, and system libraries to even start;
        // it also reads /etc/ld.so.cache + /etc/ld.so.conf.d. Without these grants
        // Landlock denies them and the app EACCESs at startup — only fully
        // self-contained (static/AOT) binaries would run. So when a sandbox IS
        // enforced we grant read+exec on the standard system dirs and read+write on
        // the handful of device nodes apps commonly need.
        //
        // Landlock is layered ON TOP OF normal DAC: granting read+exec on /etc does
        // NOT make /etc/shadow readable to a non-root user — the kernel still checks
        // file permissions first. These grants only relax the Landlock layer, never
        // the underlying ownership/mode checks. Paths that don't exist are skipped
        // (AddPath's open() fails harmlessly).
        internal static readonly IReadOnlyList<ResolvedFsRule> SystemRuntimePaths = new[]
        {
            // Loader + system libraries + binaries (read + execute, never writable).
            new ResolvedFsRule("/usr", false),
            new ResolvedFsRule("/lib", false),
            new ResolvedFsRule("/lib64", false),
            new ResolvedFsRule("/bin", false),
            new ResolvedFsRule("/sbin", false),
            new ResolvedFsRule("/etc", false),   // ld.so.cache, ld.so.conf.d, resolv.conf, …
            // Common device nodes apps open (and write to, e.g. /dev/null). Granted
            // individually — NOT all of /dev, which would expose block devices,
            // /dev/mem, etc.
            new ResolvedFsRule("/dev/null", true),
            new ResolvedFsRule("/dev/zero", true),
            new ResolvedFsRule("/dev/full", true),
            new ResolvedFsRule("/dev/random", true),
            new ResolvedFsRule("/dev/urandom", true),
            new ResolvedFsRule("/dev/tty", true),
        };

        private readonly ILogger _logger;
        private readonly int _abi;

        public LinuxLandlockSandbox(ILogger logger)
        {
            _logger = logger;
            _abi = ProbeAbi();
        }

        public bool IsSupported => _abi >= 1;

        private static int ProbeAbi()
        {
            if (!OperatingSystem.IsLinux())
            {
                return 0;
            }
            try
            {
                long r = syscall(SYS_landlock_create_ruleset, IntPtr.Zero, UIntPtr.Zero, LANDLOCK_CREATE_RULESET_VERSION);
                return r < 0 ? 0 : (int)r;
            }
            catch (DllNotFoundException)
            {
                return 0;
            }
            catch (EntryPointNotFoundException)
            {
                return 0;
            }
        }

        public int Launch(string execAbs, IReadOnlyList<string> appArgs, SandboxPlan plan, CancellationToken ct)
        {
            if (plan.Unrestricted || !IsSupported)
            {
                // Nothing to enforce (host rule) or kernel can't — just exec.
                return Exec(execAbs, appArgs);
            }

            ulong handled = HandledMask(_abi);

            // Enforce the network capability when the plan denies it AND the kernel
            // is new enough (ABI >= 4). We "handle" TCP bind+connect but add NO
            // net-port allow rules, so every TCP bind/connect is denied. On older
            // kernels we cannot enforce — log it so the app/operator knows the
            // declared denial is not actually applied here.
            ulong netHandled = 0;
            if (plan.RestrictNetwork)
            {
                if (_abi >= NET_ABI)
                {
                    netHandled = NET_BIND_TCP | NET_CONNECT_TCP;
                }
                else
                {
                    _logger.LogInformation(
                        "Sandbox requested network denial but Landlock ABI {Abi} < {NetAbi} (kernel < 6.7); network not enforced.",
                        _abi, NET_ABI);
                }
            }

            RulesetAttr attr = new RulesetAttr { handled_access_fs = handled, handled_access_net = netHandled };
            // Pass only the fs field's size on pre-net kernels so an old Landlock
            // doesn't reject a struct it doesn't understand; include the net field
            // only when we actually use it.
            UIntPtr attrSize = (UIntPtr)(netHandled != 0 ? Marshal.SizeOf<RulesetAttr>() : sizeof(ulong));
            long rulesetFd = syscall(SYS_landlock_create_ruleset, ref attr, attrSize, 0u);
            if (rulesetFd < 0)
            {
                _logger.LogWarning("Landlock ruleset creation failed; running unsandboxed.");
                return Exec(execAbs, appArgs);
            }

            try
            {
                // Grant the implicit system runtime FIRST so dynamically-linked apps
                // can load their loader + libc. DAC still applies on top (see
                // SystemRuntimePaths docs), so this does not expose /etc/shadow etc.
                foreach (ResolvedFsRule rule in SystemRuntimePaths)
                {
                    AddPath(rulesetFd, rule, handled);
                }

                foreach (ResolvedFsRule rule in plan.Rules)
                {
                    AddPath(rulesetFd, rule, handled);
                }

                if (prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) != 0)
                {
                    _logger.LogWarning("prctl(NO_NEW_PRIVS) failed; running unsandboxed.");
                    return Exec(execAbs, appArgs);
                }

                if (syscall(SYS_landlock_restrict_self, (IntPtr)rulesetFd, 0u, 0u) != 0)
                {
                    _logger.LogWarning("landlock_restrict_self failed; running unsandboxed.");
                    return Exec(execAbs, appArgs);
                }
            }
            finally
            {
                close((int)rulesetFd);
            }

            // Restrictions now bound to this process; execv inherits them.
            return Exec(execAbs, appArgs);
        }

        private void AddPath(long rulesetFd, ResolvedFsRule rule, ulong handled)
        {
            int fd = open(rule.Path, O_PATH | O_CLOEXEC, 0);
            if (fd < 0)
            {
                // Path doesn't exist / unreadable — skip; it simply won't be granted.
                _logger.LogDebug("Sandbox path '{Path}' could not be opened; skipping grant.", rule.Path);
                return;
            }
            try
            {
                // Landlock rejects (EINVAL) a path_beneath rule that carries
                // directory-only rights (READ_DIR, MAKE_*, REMOVE_*, REFER) when the
                // target is NOT a directory — e.g. a device node like /dev/null or a
                // single granted file. Such a rule fails wholesale and the path ends
                // up ungranted. So scope the access mask to what the object type
                // supports: dirs get the full set, files/devices get file rights only.
                bool isDir = Directory.Exists(rule.Path);
                ulong allowed;
                if (isDir)
                {
                    allowed = FS_READ_FILE | FS_READ_DIR | FS_EXECUTE;
                    if (rule.CanWrite)
                    {
                        allowed |= FS_WRITE_FILE | FS_REMOVE_DIR | FS_REMOVE_FILE
                                 | FS_MAKE_CHAR | FS_MAKE_DIR | FS_MAKE_REG | FS_MAKE_SOCK
                                 | FS_MAKE_FIFO | FS_MAKE_BLOCK | FS_MAKE_SYM | FS_REFER | FS_TRUNCATE;
                    }
                }
                else
                {
                    allowed = FS_READ_FILE | FS_EXECUTE;
                    if (rule.CanWrite)
                    {
                        allowed |= FS_WRITE_FILE | FS_TRUNCATE;
                    }
                }
                allowed &= handled;

                PathBeneathAttr pb = new PathBeneathAttr { allowed_access = allowed, parent_fd = fd };
                if (syscall(SYS_landlock_add_rule, (IntPtr)rulesetFd, (IntPtr)LANDLOCK_RULE_PATH_BENEATH, ref pb, 0u) != 0)
                {
                    _logger.LogWarning("Failed to add sandbox rule for '{Path}'.", rule.Path);
                }
            }
            finally
            {
                close(fd);
            }
        }

        private static ulong HandledMask(int abi)
        {
            ulong mask = FS_EXECUTE | FS_WRITE_FILE | FS_READ_FILE | FS_READ_DIR
                       | FS_REMOVE_DIR | FS_REMOVE_FILE | FS_MAKE_CHAR | FS_MAKE_DIR
                       | FS_MAKE_REG | FS_MAKE_SOCK | FS_MAKE_FIFO | FS_MAKE_BLOCK | FS_MAKE_SYM;
            if (abi >= 2) mask |= FS_REFER;
            if (abi >= 3) mask |= FS_TRUNCATE;
            return mask;
        }

        // Replace the current process image. Returns only on failure.
        private int Exec(string execAbs, IReadOnlyList<string> appArgs)
        {
            IntPtr[] argv = new IntPtr[appArgs.Count + 2];
            argv[0] = Marshal.StringToCoTaskMemUTF8(execAbs);
            for (int i = 0; i < appArgs.Count; i++)
            {
                argv[i + 1] = Marshal.StringToCoTaskMemUTF8(appArgs[i]);
            }
            argv[^1] = IntPtr.Zero;

            execv(execAbs, argv);
            // If we got here, execv failed.
            int err = Marshal.GetLastPInvokeError();
            foreach (IntPtr p in argv)
            {
                if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
            }
            throw new InvalidOperationException($"Failed to exec '{execAbs}' (errno {err}).");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RulesetAttr
        {
            public ulong handled_access_fs;
            public ulong handled_access_net; // ABI v4+; passed only when used (see attrSize).
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct PathBeneathAttr
        {
            public ulong allowed_access;
            public int parent_fd;
        }

        [DllImport("libc", SetLastError = true)]
        private static extern long syscall(long number, IntPtr arg1, UIntPtr arg2, uint arg3);

        [DllImport("libc", SetLastError = true)]
        private static extern long syscall(long number, ref RulesetAttr attr, UIntPtr size, uint flags);

        [DllImport("libc", SetLastError = true)]
        private static extern long syscall(long number, IntPtr rulesetFd, IntPtr ruleType, ref PathBeneathAttr attr, uint flags);

        [DllImport("libc", SetLastError = true)]
        private static extern long syscall(long number, IntPtr rulesetFd, uint flags, uint unused);

        [DllImport("libc", SetLastError = true)]
        private static extern int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);

        [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);

        [DllImport("libc", SetLastError = true)]
        private static extern int close(int fd);

        [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern int execv([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr[] argv);
    }
}
