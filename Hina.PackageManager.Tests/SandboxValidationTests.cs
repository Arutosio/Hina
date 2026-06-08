using System.Collections.Generic;
using Hina.Core.Crypto;
using Hina.PackageManager.Descriptor;

namespace Hina.PackageManager.Tests
{
    public class SandboxValidationTests
    {
        private static AppDescriptor WithSandbox(SandboxSpec? sandbox)
        {
            (_, string pub) = KeyGenerator.GenerateEd25519();
            return new AppDescriptor
            {
                SchemaVersion = 1,
                Name = "demo",
                DisplayName = "Demo",
                Version = "1.0.0",
                Publisher = "Acme",
                Channel = "stable",
                BaseUrl = "https://example.com/demo/",
                PublicKey = pub,
                Exec = new ExecMap { Linux = "bin/demo" },
                Sandbox = sandbox,
            };
        }

        [Fact]
        public void Validate_NoSandbox_IsValid()
        {
            Assert.True(DescriptorValidator.Validate(WithSandbox(null)).IsValid);
        }

        [Fact]
        public void Validate_SandboxDisabledEmpty_IsValid()
        {
            Assert.True(DescriptorValidator.Validate(WithSandbox(new SandboxSpec { Enabled = false })).IsValid);
        }

        [Theory]
        [InlineData("home", "ro")]
        [InlineData("xdg-documents", "rw")]
        [InlineData("app", "ro")]
        [InlineData("host", "rw")]
        public void Validate_KnownTokenAndAccess_IsValid(string path, string access)
        {
            SandboxSpec s = new SandboxSpec
            {
                Enabled = true,
                Filesystem = new List<FsRule> { new FsRule { Path = path, Access = access } },
            };
            Assert.True(DescriptorValidator.Validate(WithSandbox(s)).IsValid);
        }

        [Fact]
        public void Validate_UnknownToken_IsRejected()
        {
            SandboxSpec s = new SandboxSpec
            {
                Enabled = true,
                Filesystem = new List<FsRule> { new FsRule { Path = "/etc/passwd", Access = "ro" } },
            };
            ValidationResult r = DescriptorValidator.Validate(WithSandbox(s));
            Assert.False(r.IsValid);
            Assert.Contains(r.Errors, e => e.Contains("sandbox"));
        }

        [Fact]
        public void Validate_BadAccess_IsRejected()
        {
            SandboxSpec s = new SandboxSpec
            {
                Enabled = true,
                Filesystem = new List<FsRule> { new FsRule { Path = "home", Access = "rwx" } },
            };
            ValidationResult r = DescriptorValidator.Validate(WithSandbox(s));
            Assert.False(r.IsValid);
            Assert.Contains(r.Errors, e => e.Contains("sandbox"));
        }
    }
}
