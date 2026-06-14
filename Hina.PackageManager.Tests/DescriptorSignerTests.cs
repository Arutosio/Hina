using System;
using System.Collections.Generic;
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

        // Variant of SampleDescriptor that carries a fully populated Sandbox block, used by the
        // security regression tests for BUG-001 (Sandbox was excluded from canonical bytes).
        private static AppDescriptor SampleDescriptorWithSandbox(string publicKey) => new AppDescriptor
        {
            SchemaVersion = 1,
            Name = "sandboxed-demo",
            DisplayName = "Sandboxed Demo",
            Version = "1.0.0",
            Publisher = "Acme",
            BaseUrl = "https://example.com/sandboxed-demo/",
            PublicKey = publicKey,
            Exec = new ExecMap { Linux = "bin/demo" },
            Entries = { new ShellEntry { Id = "main", Name = "Sandboxed Demo", Exec = "bin/demo" } },
            Sandbox = new SandboxSpec
            {
                Enabled = true,
                Filesystem = new List<FsRule>
                {
                    new FsRule { Path = "home", Access = "ro" }
                },
                Capabilities = new CapabilitySpec { Network = true }
            }
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

        // --- Security regression tests for BUG-001 ---
        // Before the fix, GetCanonicalBytes omitted Sandbox from the signed payload, so any
        // in-transit mutation of the sandbox block would pass Ed25519 verification undetected.

        // (1) Flipping Sandbox.Enabled after signing must invalidate the signature.
        //     Before the fix this would have passed verification (sandbox was not signed).
        [Fact]
        public void Verify_AfterFlippingSandboxEnabled_ReturnsFalse()
        {
            (string priv, string pub) = KeyGenerator.GenerateEd25519();
            AppDescriptor d = SampleDescriptorWithSandbox(pub);
            DescriptorSigner.AttachSignature(d, Convert.FromBase64String(priv));

            // Attacker disables the sandbox enforcement in transit.
            d.Sandbox!.Enabled = false;

            Assert.False(DescriptorSigner.Verify(d, pub));
        }

        // (2) Injecting a host-access FsRule after signing must invalidate the signature.
        //     "host" is the SandboxTokens token that grants unrestricted filesystem access.
        [Fact]
        public void Verify_AfterInjectingHostFsRule_ReturnsFalse()
        {
            (string priv, string pub) = KeyGenerator.GenerateEd25519();
            AppDescriptor d = SampleDescriptorWithSandbox(pub);
            DescriptorSigner.AttachSignature(d, Convert.FromBase64String(priv));

            // Attacker appends an unrestricted "host" rule to bypass Landlock.
            d.Sandbox!.Filesystem.Add(new FsRule { Path = "host", Access = "rw" });

            Assert.False(DescriptorSigner.Verify(d, pub));
        }

        // (3) Stripping the entire Sandbox block after signing must invalidate the signature
        //     (no sandbox-downgrade / isolation-removal attack).
        [Fact]
        public void Verify_AfterRemovingSandbox_ReturnsFalse()
        {
            (string priv, string pub) = KeyGenerator.GenerateEd25519();
            AppDescriptor d = SampleDescriptorWithSandbox(pub);
            DescriptorSigner.AttachSignature(d, Convert.FromBase64String(priv));

            // Attacker nulls out the sandbox block entirely.
            d.Sandbox = null;

            Assert.False(DescriptorSigner.Verify(d, pub));
        }

        // (4) Sign→Verify round-trip on a descriptor WITH a Sandbox block must succeed
        //     (the fix must not break legitimate descriptors that carry a sandbox).
        [Fact]
        public void Sign_ThenVerify_WithSandbox_ReturnsTrue()
        {
            (string priv, string pub) = KeyGenerator.GenerateEd25519();
            AppDescriptor d = SampleDescriptorWithSandbox(pub);
            DescriptorSigner.AttachSignature(d, Convert.FromBase64String(priv));

            Assert.True(DescriptorSigner.Verify(d, pub));
        }

        // (5) Serialize→Parse round-trip on a sandbox-bearing signed descriptor must keep
        //     the signature valid (mirrors Sign_SurvivesSerializationRoundTrip for sandbox).
        [Fact]
        public void Sign_WithSandbox_SurvivesSerializationRoundTrip()
        {
            (string priv, string pub) = KeyGenerator.GenerateEd25519();
            AppDescriptor d = SampleDescriptorWithSandbox(pub);
            DescriptorSigner.AttachSignature(d, Convert.FromBase64String(priv));

            string json = DescriptorParser.Serialize(d);
            AppDescriptor reparsed = DescriptorParser.Parse(json);

            Assert.True(DescriptorSigner.Verify(reparsed, pub));
        }
    }
}
