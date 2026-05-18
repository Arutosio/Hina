using System;
using System.Runtime.InteropServices;
using Hina.PackageManager.Paths;
using Hina.PackageManager.Platform.Linux;
using Hina.PackageManager.Platform.MacOS;
using Hina.PackageManager.Platform.Windows;

namespace Hina.PackageManager.Platform
{
    public static class PlatformIntegrationFactory
    {
        public static IPlatformIntegration Current(InstallPaths paths)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return new LinuxPlatformIntegration(paths);
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return new MacOSPlatformIntegration(paths);
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return new WindowsPlatformIntegration(paths);
            }
            throw new PlatformNotSupportedException("This OS is not supported by Hina.");
        }
    }
}
