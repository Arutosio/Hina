using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Hina.PackageManager.Sandbox
{
    // Selects the sandbox backend for this OS. Linux → Landlock when the kernel
    // supports it; macOS → sandbox-exec (Seatbelt); otherwise NoOp. Windows → NoOp
    // until its backend lands. A backend that reports IsSupported=false falls back
    // to NoOp so a launch is never blocked.
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
            return new NoOpSandbox(logger);
        }
    }
}
