using System.Collections.Generic;
using System.Linq;
using Hina.Core.Crypto;
using Hina.PackageManager.Descriptor;

namespace Hina.PackageManager.Tests
{
    public sealed class DescriptorValidatorPlatformsTests
    {
        private static AppDescriptor Base(List<PlatformVariant> platforms) => new AppDescriptor
        {
            Name = "mygame",
            DisplayName = "My Game",
            Version = "1.0.0",
            Publisher = "Acme",
            Channel = "stable",
            BaseUrl = "https://patch.example.com/",
            PublicKey = KeyGenerator.GenerateEd25519().PublicKeyBase64,
            Exec = new ExecMap(),       // no legacy exec map; platforms are authoritative
            Platforms = platforms
        };

        [Fact]
        public void PlatformsOnly_NoExecMap_IsValid()
        {
            AppDescriptor d = Base(new List<PlatformVariant>
            {
                new PlatformVariant { Os = "windows", Arch = "x64", Exec = "Game.exe" },
                new PlatformVariant { Os = "macos", Arch = "arm64", Exec = "G.app/Contents/MacOS/G" },
                new PlatformVariant { Os = "linux", Exec = "game" }
            });
            Assert.True(DescriptorValidator.Validate(d).IsValid);
        }

        [Fact]
        public void UnknownOs_IsRejected()
        {
            AppDescriptor d = Base(new List<PlatformVariant> { new PlatformVariant { Os = "solaris", Exec = "g" } });
            Assert.Contains(DescriptorValidator.Validate(d).Errors, e => e.Contains("os"));
        }

        [Fact]
        public void UnknownArch_IsRejected()
        {
            AppDescriptor d = Base(new List<PlatformVariant> { new PlatformVariant { Os = "linux", Arch = "riscv", Exec = "g" } });
            Assert.Contains(DescriptorValidator.Validate(d).Errors, e => e.Contains("arch"));
        }

        [Fact]
        public void DuplicateOsArch_IsRejected()
        {
            AppDescriptor d = Base(new List<PlatformVariant>
            {
                new PlatformVariant { Os = "macos", Arch = "x64", Exec = "a" },
                new PlatformVariant { Os = "macos", Arch = "x64", Exec = "b" }
            });
            Assert.Contains(DescriptorValidator.Validate(d).Errors, e => e.Contains("duplicates"));
        }

        [Fact]
        public void AbsoluteExec_IsRejected()
        {
            AppDescriptor d = Base(new List<PlatformVariant> { new PlatformVariant { Os = "linux", Exec = "/usr/bin/game" } });
            Assert.False(DescriptorValidator.Validate(d).IsValid);
        }

        [Fact]
        public void NoExecMapAndNoPlatforms_IsRejected()
        {
            AppDescriptor d = Base(new List<PlatformVariant>());
            Assert.Contains(DescriptorValidator.Validate(d).Errors, e => e.Contains("platforms"));
        }
    }
}
