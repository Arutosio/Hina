using System.Runtime.InteropServices;

namespace Hina.PackageManager.Platform
{
    // The current machine's platform identity and the token used to name a variant's manifest.
    // os ∈ {windows, macos, linux}; arch ∈ {x64, arm64, x86, arm}. Token = "os" or "os-arch".
    public static class PlatformToken
    {
        public static string CurrentOs()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macos";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux";
            return "unknown";
        }

        public static string CurrentArch() => RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()
        };

        // Manifest token for a (resolved) variant: "os" when no arch, else "os-arch".
        public static string Token(string os, string? arch)
            => string.IsNullOrEmpty(arch) ? os : $"{os}-{arch}";
    }
}
