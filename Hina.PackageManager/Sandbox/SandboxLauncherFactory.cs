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
                // AppContainer runtime DACL grants are honoured in a normal interactive
                // desktop session (the supported enforcement target). Environment.UserInteractive
                // screens out true session-0 service contexts (no window station), where the
                // lowbox is created without error but every grant is silently ignored — there we
                // fall through to NoOp (runs unsandboxed + one-time warning) rather than launch an
                // app denied its own files. NOTE this screen is PARTIAL: some windowed-but-headless
                // contexts (notably the GitHub windows-latest CI runner) still report
                // UserInteractive == true and reach the backend with grants ignored; the
                // windows-sandbox CI probe covers those by SKIP-ing on $env:CI.
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
