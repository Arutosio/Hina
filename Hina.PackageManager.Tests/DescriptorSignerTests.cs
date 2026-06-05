using System;
using Hina.Core.Crypto;
using Hina.PackageManager.Descriptor;

namespace Hina.PackageManager.Tests
{
    public class DescriptorSignerTests
    {
        private static AppDescriptor SampleDescriptor(string publicKey) => new AppDescriptor
        {
            SchemaVersion = 1,
            Name = "demo",
            DisplayName = "Demo",
            Version = "1.0.0",
            Publisher = "Acme",
            BaseUrl = "https://example.com/demo/",
            PublicKey = publicKey,
            Exec = new ExecMap { Linux = "bin/demo" },
            Entries = { new ShellEntry { Id = "main", Name = "Demo", Exec = "bin/demo" } }
        };

        [Fact]
        public void Sign_ThenVerify_WithMatchingKey_ReturnsTrue()
        {
            (string priv, string pub) = KeyGenerator.GenerateEd25519();
            AppDescriptor d = SampleDescriptor(pub);

            DescriptorSigner.AttachSignature(d, Convert.FromBase64String(priv));

            Assert.True(DescriptorSigner.Verify(d, pub));
        }

        [Fact]
        public void Verify_WithDifferentKey_ReturnsFalse()
        {
            (string priv1, string pub1) = KeyGenerator.GenerateEd25519();
            (_, string pub2) = KeyGenerator.GenerateEd25519();

            AppDescriptor d = SampleDescriptor(pub1);
            DescriptorSigner.AttachSignature(d, Convert.FromBase64String(priv1));

            Assert.False(DescriptorSigner.Verify(d, pub2));
        }

        [Fact]
        public void Verify_AfterTampering_ReturnsFalse()
        {
            (string priv, string pub) = KeyGenerator.GenerateEd25519();
            AppDescriptor d = SampleDescriptor(pub);
            DescriptorSigner.AttachSignature(d, Convert.FromBase64String(priv));

            d.Version = "9.9.9";

            Assert.False(DescriptorSigner.Verify(d, pub));
        }

        [Fact]
        public void Verify_WithoutSignature_ReturnsFalse()
        {
            (_, string pub) = KeyGenerator.GenerateEd25519();
            AppDescriptor d = SampleDescriptor(pub);

            Assert.False(DescriptorSigner.Verify(d, pub));
        }

        [Fact]
        public void Verify_NonEd25519Algorithm_ReturnsFalse()
        {
            (string priv, string pub) = KeyGenerator.GenerateEd25519();
            AppDescriptor d = SampleDescriptor(pub);
            DescriptorSigner.AttachSignature(d, Convert.FromBase64String(priv));

            // Tamper only the declared algorithm — the signature bytes are still valid Ed25519,
            // but a mismatched algorithm must be rejected (no silent downgrade/confusion).
            d.DescriptorSignature!.Algorithm = "rsa";

            Assert.False(DescriptorSigner.Verify(d, pub));
        }

        [Fact]
        public void Verify_MalformedKeyOrSignature_ReturnsFalse_DoesNotThrow()
        {
            (string priv, string pub) = KeyGenerator.GenerateEd25519();
            AppDescriptor d = SampleDescriptor(pub);
            DescriptorSigner.AttachSignature(d, Convert.FromBase64String(priv));

            // 32 valid base64 bytes that are not a real public key — must fail, not crash.
            string junkKey = Convert.ToBase64String(new byte[32]);
            Assert.False(DescriptorSigner.Verify(d, junkKey));

            // Wrong-length signature (valid base64, decodes to <64 bytes) must fail cleanly.
            d.DescriptorSignature!.Signature = Convert.ToBase64String(new byte[10]);
            Assert.False(DescriptorSigner.Verify(d, pub));

            // Empty / non-base64 trusted key must fail cleanly.
            Assert.False(DescriptorSigner.Verify(d, ""));
            Assert.False(DescriptorSigner.Verify(d, "!!!not base64!!!"));
        }

        [Fact]
        public void Sign_SurvivesSerializationRoundTrip()
        {
            (string priv, string pub) = KeyGenerator.GenerateEd25519();
            AppDescriptor d = SampleDescriptor(pub);
            DescriptorSigner.AttachSignature(d, Convert.FromBase64String(priv));

            string json = DescriptorParser.Serialize(d);
            AppDescriptor reparsed = DescriptorParser.Parse(json);

            Assert.True(DescriptorSigner.Verify(reparsed, pub));
        }
    }
}
