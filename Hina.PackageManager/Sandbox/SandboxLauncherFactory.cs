using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Hina.PackageManager.Sandbox
{
    // Selects the sandbox backend for this OS. Linux → Landlock when the kernel
    // supports it, otherwise NoOp. macOS/Windows → NoOp until their backends land.
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
            return new NoOpSandbox(logger);
        }
    }
}
