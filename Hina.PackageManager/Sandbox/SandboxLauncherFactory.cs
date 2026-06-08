using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Hina.PackageManager.Sandbox
{
    // Selects the sandbox backend for this OS. Linux → Landlock when the kernel
    // supports it; macOS → sandbox-exec (Seatbelt); otherwise NoOp. A backend that
    // reports IsSupported=false falls back to NoOp so a launch is never blocked.
    //
    // Windows: the AppContainer backend (WindowsSandbox) is implemented but deliberately
    // NOT selected — see its header. On the windows-latest CI probe the lowbox honored
    // no runtime DACL grant (package SID, ALL APPLICATION PACKAGES, or lowered integrity,
    // shallow or deep path all denied), so wiring it in would launch apps that can't read
    // their own files. Until that is resolved on a real Windows box, Windows stays NoOp.
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
            // Windows intentionally falls through to NoOp — WindowsSandbox is unverified
            // (see header) and selecting it would break app launches.
            return new NoOpSandbox(logger);
        }
    }
}
