using System.Collections.Generic;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Install;
using Hina.PackageManager.Platform;

namespace Hina.PackageManager.Tests
{
    public sealed class PlatformResolverTests
    {
        // Legacy app: no platforms[], just the OS exec map → single manifest (null token).
        [Fact]
        public void Legacy_UsesExecMap_NullToken()
        {
            AppDescriptor d = new AppDescriptor
            {
                Exec = new ExecMap { Windows = "g.exe", Linux = "g", Macos = "g.app" }
            };
            PlatformResolution r = PlatformResolver.ForCurrentMachine(d);
            Assert.Null(r.ManifestToken);
            Assert.False(r.NoBuildForPlatform);
            // Exec resolves to the current OS's entry (whatever this test host is).
            Assert.NotNull(r.ExecRelative);
        }

        // Multi-variant app with an OS-only variant for the current OS always resolves.
        [Fact]
        public void Multi_OsOnlyForCurrentOs_Resolves()
        {
            AppDescriptor d = new AppDescriptor
            {
                Platforms = new List<PlatformVariant>
                {
                    new PlatformVariant { Os = PlatformToken.CurrentOs(), Exec = "game" }
                }
            };
            PlatformResolution r = PlatformResolver.ForCurrentMachine(d);
            Assert.Equal(PlatformToken.CurrentOs(), r.ManifestToken);
            Assert.Equal("game", r.ExecRelative);
        }

        [Fact]
        public void Multi_NoVariantForOs_FlagsNoBuild()
        {
            // A descriptor that only ships an OS that is NOT the test host.
            string otherOs = PlatformToken.CurrentOs() == "linux" ? "windows" : "linux";
            AppDescriptor d = new AppDescriptor
            {
                Platforms = new List<PlatformVariant> { new PlatformVariant { Os = otherOs, Exec = "g" } }
            };
            PlatformResolution r = PlatformResolver.ForCurrentMachine(d);
            Assert.True(r.NoBuildForPlatform);
        }

        [Fact]
        public void ForInstalledToken_ExactToken_ResolvesThatVariant()
        {
            AppDescriptor d = new AppDescriptor
            {
                Platforms = new List<PlatformVariant>
                {
                    new PlatformVariant { Os = "macos", Arch = "x64", Exec = "x64app" },
                    new PlatformVariant { Os = "macos", Arch = "arm64", Exec = "armapp" }
                }
            };
            PlatformResolution r = PlatformResolver.ForInstalledToken(d, "macos-x64");
            Assert.Equal("macos-x64", r.ManifestToken);
            Assert.Equal("x64app", r.ExecRelative);
        }

        [Fact]
        public void ForInstalledToken_LegacyEmptyToken_UsesExecMap()
        {
            AppDescriptor d = new AppDescriptor { Exec = new ExecMap { Linux = "g", Macos = "g", Windows = "g.exe" } };
            PlatformResolution r = PlatformResolver.ForInstalledToken(d, "");
            Assert.Null(r.ManifestToken);
            Assert.NotNull(r.ExecRelative);
        }
    }
}
