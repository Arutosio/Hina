using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Hina.PackageManager.Sandbox
{
    // Selects the sandbox backend for this OS. Linux → Landlock when the kernel
    // supports it; macOS → sandbox-exec (Seatbelt); Windows → AppContainer (WindowsSandbox);
    // otherwise NoOp. A backend that reports IsSupported=false falls back to NoOp so a launch
    // is never blocked.
    public static class SandboxLauncherFactory
    {
        public static ISandboxLauncher Current(ILogger logger)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                LinuxLandlockSandbox landlock = new LinuxLandlockSandbox(logger);
                if (landlock.IsSupported)
                {
                    return landlock;
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                MacOsSandbox macos = new MacOsSandbox(logger);
                if (macos.IsSupported)
                {
                    return macos;
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // AppContainer runtime DACL grants are only honoured in an interactive
                // desktop session. In a non-interactive / service context (session 0, the
                // GitHub windows-latest CI runner, Windows Server services) the lowbox is
                // created without error but every grant is silently ignored, so the app
                // would launch unable to read its own files. Don't claim enforcement there —
                // fall through to NoOp (runs unsandboxed + one-time warning) instead.
                if (Environment.UserInteractive)
                {
                    WindowsSandbox windows = new WindowsSandbox(logger);
                    if (windows.IsSupported)
                    {
                        return windows;
                    }
                }
            }
            return new NoOpSandbox(logger);
        }
    }
}
