using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Hina.PackageManager.Sandbox
{
    // Selects the sandbox backend for this OS. Linux → Landlock when the kernel
    // supports it; macOS → sandbox-exec (Seatbelt); Windows → AppContainer (NT 6.2+);
    // otherwise NoOp. A backend that reports IsSupported=false falls back to NoOp so a
    // launch is never blocked.
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
                WindowsSandbox windows = new WindowsSandbox(logger);
                if (windows.IsSupported)
                {
                    return windows;
                }
            }
            return new NoOpSandbox(logger);
        }
    }
}
