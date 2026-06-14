using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Platform;

namespace Hina.PackageManager.Install
{
    // The outcome of mapping a descriptor to a concrete machine: which executable to run, which
    // manifest to fetch, and whether a compatibility fallback was used. Unifies the multi-variant
    // (descriptor.Platforms) and legacy (descriptor.Exec map) cases so install/update/run/verify
    // all resolve the same way.
    public sealed class PlatformResolution
    {
        // Executable path relative to the install dir, or null when the descriptor has none for
        // this platform.
        public string? ExecRelative { get; init; }

        // Variant token selecting manifest.<token>.json, or null for a legacy single-manifest app
        // (manifest.json).
        public string? ManifestToken { get; init; }

        // True when an x64 build was chosen for an arm64 host (Rosetta/emulation) — warn the user.
        public bool UsedFallback { get; init; }

        // True when the app is multi-variant but no variant can serve this OS/arch at all.
        public bool NoBuildForPlatform { get; init; }
    }

    public static class PlatformResolver
    {
        // Install time: pick the best variant for the machine running Hina now.
        public static PlatformResolution ForCurrentMachine(AppDescriptor descriptor)
        {
            if (descriptor.Platforms.Count == 0)
            {
                // Legacy single-manifest app. Flag NoBuildForPlatform when the Exec map has no
                // entry for this OS (BUG-042) so install fails fast BEFORE downloading the whole
                // payload — symmetric with the multi-variant branch below.
                string? legacyExec = InstallService.ExecForCurrentOs(descriptor);
                return new PlatformResolution
                {
                    ExecRelative = legacyExec,
                    ManifestToken = null,
                    NoBuildForPlatform = legacyExec == null
                };
            }

            PlatformSelection? sel = PlatformSelector.Select(
                descriptor.Platforms, PlatformToken.CurrentOs(), PlatformToken.CurrentArch());
            if (sel == null)
            {
                return new PlatformResolution { NoBuildForPlatform = true };
            }
            return new PlatformResolution
            {
                ExecRelative = sel.Variant.Exec,
                ManifestToken = sel.Token,
                UsedFallback = sel.UsedFallback
            };
        }

        // Update/verify/run time: stick to the variant that was installed (token from the
        // registry) so an update fetches the same manifest. Falls back to a fresh selection if the
        // app is legacy, the registry predates variants, or the variant disappeared from the
        // descriptor.
        public static PlatformResolution ForInstalledToken(AppDescriptor descriptor, string? installedToken)
        {
            if (descriptor.Platforms.Count == 0)
            {
                // Same legacy resolution (incl. the NoBuildForPlatform flag) as install time.
                return ForCurrentMachine(descriptor);
            }
            if (!string.IsNullOrEmpty(installedToken))
            {
                foreach (PlatformVariant v in descriptor.Platforms)
                {
                    if (PlatformToken.Token(v.Os, v.Arch) == installedToken)
                    {
                        return new PlatformResolution { ExecRelative = v.Exec, ManifestToken = installedToken };
                    }
                }
            }
            return ForCurrentMachine(descriptor);
        }
    }
}
