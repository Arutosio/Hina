using Hina.Core.Crypto;
using Hina.PackageManager.Descriptor;

namespace Hina.PackageManager.Tests
{
    public class DescriptorValidatorChannelTests
    {
        private static AppDescriptor Valid(string channel)
        {
            (_, string pub) = KeyGenerator.GenerateEd25519();
            return new AppDescriptor
            {
                SchemaVersion = 1,
                Name = "demo",
                DisplayName = "Demo",
                Version = "1.0.0",
                Publisher = "Acme",
                Channel = channel,
                BaseUrl = "https://example.com/demo/",
                PublicKey = pub,
                Exec = new ExecMap { Linux = "bin/demo" }
            };
        }

        [Theory]
        [InlineData("stable")]
        [InlineData("beta")]
        [InlineData("2.x")]
        [InlineData("nightly-2026")]
        public void Validate_SafeChannel_IsValid(string channel)
        {
            Assert.True(DescriptorValidator.Validate(Valid(channel)).IsValid);
        }

        [Theory]
        [InlineData("../../etc")]
        [InlineData("a/b")]
        [InlineData("has space")]
        [InlineData("")]
        public void Validate_UnsafeChannel_IsRejected(string channel)
        {
            ValidationResult r = DescriptorValidator.Validate(Valid(channel));
            Assert.False(r.IsValid);
            Assert.Contains(r.Errors, e => e.Contains("channel"));
        }
    }
}
