using System.Collections.Generic;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Platform;

namespace Hina.PackageManager.Tests
{
    public sealed class PlatformSelectorTests
    {
        private static List<PlatformVariant> Variants(params (string os, string? arch)[] specs)
        {
            List<PlatformVariant> list = new List<PlatformVariant>();
            foreach ((string os, string? arch) in specs)
            {
                list.Add(new PlatformVariant { Os = os, Arch = arch, Exec = "app" });
            }
            return list;
        }

        [Fact]
        public void ExactMatch_Wins()
        {
            var sel = PlatformSelector.Select(Variants(("macos", "x64"), ("macos", "arm64")), "macos", "arm64");
            Assert.NotNull(sel);
            Assert.Equal("arm64", sel!.Variant.Arch);
            Assert.False(sel.UsedFallback);
            Assert.Equal("macos-arm64", sel.Token);
        }

        [Fact]
        public void OsOnlyVariant_MatchesAnyArch()
        {
            var sel = PlatformSelector.Select(Variants(("linux", null)), "linux", "arm64");
            Assert.NotNull(sel);
            Assert.Null(sel!.Variant.Arch);
            Assert.False(sel.UsedFallback);
            Assert.Equal("linux", sel.Token);
        }

        [Fact]
        public void MacArm64_FallsBackToX64_WithFlag()
        {
            var sel = PlatformSelector.Select(Variants(("macos", "x64")), "macos", "arm64");
            Assert.NotNull(sel);
            Assert.Equal("x64", sel!.Variant.Arch);
            Assert.True(sel.UsedFallback);
            Assert.Equal("macos-x64", sel.Token);
        }

        [Fact]
        public void WindowsArm64_FallsBackToX64()
        {
            var sel = PlatformSelector.Select(Variants(("windows", "x64")), "windows", "arm64");
            Assert.NotNull(sel);
            Assert.True(sel!.UsedFallback);
        }

        [Fact]
        public void LinuxArm64_NoX64Fallback()
        {
            // Linux has no transparent x86-64 emulation, so an arm64 host gets no x64 fallback.
            var sel = PlatformSelector.Select(Variants(("linux", "x64")), "linux", "arm64");
            Assert.Null(sel);
        }

        [Fact]
        public void NoVariantForOs_ReturnsNull()
        {
            var sel = PlatformSelector.Select(Variants(("windows", "x64")), "macos", "arm64");
            Assert.Null(sel);
        }

        [Fact]
        public void ExactPreferredOverFallback()
        {
            // Both an x64 and a native arm64 build exist → pick arm64, no fallback.
            var sel = PlatformSelector.Select(Variants(("macos", "x64"), ("macos", "arm64")), "macos", "arm64");
            Assert.False(sel!.UsedFallback);
            Assert.Equal("arm64", sel.Variant.Arch);
        }
    }

    public sealed class PlatformTokenTests
    {
        [Fact]
        public void Token_OsOnly_OmitsArch()
        {
            Assert.Equal("linux", PlatformToken.Token("linux", null));
            Assert.Equal("linux", PlatformToken.Token("linux", ""));
        }

        [Fact]
        public void Token_OsArch_Joins()
        {
            Assert.Equal("macos-arm64", PlatformToken.Token("macos", "arm64"));
        }

        [Fact]
        public void CurrentOsAndArch_AreNonEmpty()
        {
            Assert.False(string.IsNullOrEmpty(PlatformToken.CurrentOs()));
            Assert.False(string.IsNullOrEmpty(PlatformToken.CurrentArch()));
        }
    }
}
