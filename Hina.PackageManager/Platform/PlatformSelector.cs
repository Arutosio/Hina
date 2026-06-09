using System.Collections.Generic;
using Hina.PackageManager.Descriptor;

namespace Hina.PackageManager.Platform
{
    // The variant chosen for a machine, plus whether a compatibility fallback was used (so the
    // caller can warn the user that, e.g., an x64 build will run under Rosetta/emulation).
    public sealed class PlatformSelection
    {
        public PlatformVariant Variant { get; }
        public bool UsedFallback { get; }
        public string Token => PlatformToken.Token(Variant.Os, Variant.Arch);

        public PlatformSelection(PlatformVariant variant, bool usedFallback)
        {
            Variant = variant;
            UsedFallback = usedFallback;
        }
    }

    // Picks the best variant for a machine's (os, arch). Returns null when no variant can serve
    // the OS at all — the caller turns that into a clean "no build for your platform" error.
    public static class PlatformSelector
    {
        // An arm64 host can transparently run the x64 build: macOS via Rosetta 2, Windows via
        // its x64 emulator. (Linux has no in-box transparent x86-64 emulation, so it's excluded.)
        private static readonly IReadOnlyDictionary<string, string> ArchFallback =
            new Dictionary<string, string> { ["arm64"] = "x64" };

        public static PlatformSelection? Select(IReadOnlyList<PlatformVariant> platforms, string os, string arch)
        {
            // 1. Exact os + arch.
            foreach (PlatformVariant v in platforms)
            {
                if (v.Os == os && v.Arch == arch) return new PlatformSelection(v, usedFallback: false);
            }

            // 2. OS-only variant (universal build for this OS).
            foreach (PlatformVariant v in platforms)
            {
                if (v.Os == os && v.Arch == null) return new PlatformSelection(v, usedFallback: false);
            }

            // 3. Compatible-arch fallback (arm64 → x64) on the OSes that emulate it.
            if (ArchFallback.TryGetValue(arch, out string? compatArch) && (os == "macos" || os == "windows"))
            {
                foreach (PlatformVariant v in platforms)
                {
                    if (v.Os == os && v.Arch == compatArch) return new PlatformSelection(v, usedFallback: true);
                }
            }

            // 4. Nothing serves this OS/arch.
            return null;
        }
    }
}
