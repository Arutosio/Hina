using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Hina.Core.Manifest;
using Xunit;

namespace Hina.Core.Tests
{
    public class ManifestSignerTests
    {
        [Fact]
        public void AttachSignature_SetsSignatureFields()
        {
            Manifest.Manifest manifest = CreateTestManifest();
            byte[] privateKey = GeneratePrivateKey();

            ManifestSigner.AttachSignature(manifest, privateKey);

            Assert.NotNull(manifest.Signature);
            Assert.Equal("ed25519", manifest.Signature!.Algorithm);
            Assert.False(string.IsNullOrEmpty(manifest.Signature.Signature));
            Assert.False(string.IsNullOrEmpty(manifest.Signature.PublicKey));
        }

        [Fact]
        public void Verify_ValidSignature_ReturnsTrue()
        {
            Manifest.Manifest manifest = CreateTestManifest();
            byte[] privateKey = GeneratePrivateKey();

            ManifestSigner.AttachSignature(manifest, privateKey);
            bool result = ManifestSigner.Verify(manifest, manifest.Signature!.PublicKey);

            Assert.True(result);
        }

        [Fact]
        public void Verify_NullSignature_ReturnsFalse()
        {
            Manifest.Manifest manifest = CreateTestManifest();
            manifest.Signature = null;

            bool result = ManifestSigner.Verify(manifest, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");

            Assert.False(result);
        }

        [Fact]
        public void Verify_TamperedManifest_ReturnsFalse()
        {
            Manifest.Manifest manifest = CreateTestManifest();
            byte[] privateKey = GeneratePrivateKey();

            ManifestSigner.AttachSignature(manifest, privateKey);
            string publicKey = manifest.Signature!.PublicKey;

            // Tamper with manifest after signing
            manifest.Version = "99.99.99";

            bool result = ManifestSigner.Verify(manifest, publicKey);
            Assert.False(result);
        }

        [Fact]
        public void Verify_WrongPublicKey_ReturnsFalse()
        {
            Manifest.Manifest manifest = CreateTestManifest();
            byte[] privateKey1 = GeneratePrivateKey();
            byte[] privateKey2 = GeneratePrivateKey();

            ManifestSigner.AttachSignature(manifest, privateKey1);

            // Get public key from a different key pair
            Manifest.Manifest dummy = CreateTestManifest();
            ManifestSigner.AttachSignature(dummy, privateKey2);
            string wrongPublicKey = dummy.Signature!.PublicKey;

            bool result = ManifestSigner.Verify(manifest, wrongPublicKey);
            Assert.False(result);
        }

        [Fact]
        public void AttachSignature_DifferentKeys_ProduceDifferentSignatures()
        {
            Manifest.Manifest m1 = CreateTestManifest();
            Manifest.Manifest m2 = CreateTestManifest();

            ManifestSigner.AttachSignature(m1, GeneratePrivateKey());
            ManifestSigner.AttachSignature(m2, GeneratePrivateKey());

            Assert.NotEqual(m1.Signature!.Signature, m2.Signature!.Signature);
        }

        [Fact]
        public void AttachSignature_SameKey_SameManifest_ProducesSameSignature()
        {
            byte[] privateKey = GeneratePrivateKey();

            Manifest.Manifest m1 = CreateTestManifest();
            m1.BuildId = DateTimeOffset.UnixEpoch; // fixed
            ManifestSigner.AttachSignature(m1, privateKey);

            Manifest.Manifest m2 = CreateTestManifest();
            m2.BuildId = DateTimeOffset.UnixEpoch; // fixed
            ManifestSigner.AttachSignature(m2, privateKey);

            Assert.Equal(m1.Signature!.Signature, m2.Signature!.Signature);
        }

        [Fact]
        public void Verify_NonEd25519Algorithm_ReturnsFalse()
        {
            Manifest.Manifest manifest = CreateTestManifest();
            ManifestSigner.AttachSignature(manifest, GeneratePrivateKey());
            string publicKey = manifest.Signature!.PublicKey;

            // Downgrade/confusion: a valid Ed25519 signature relabeled as another algorithm.
            manifest.Signature.Algorithm = "rsa";

            Assert.False(ManifestSigner.Verify(manifest, publicKey));
        }

        [Fact]
        public void Verify_MalformedBase64Signature_ReturnsFalse()
        {
            Manifest.Manifest manifest = CreateTestManifest();
            ManifestSigner.AttachSignature(manifest, GeneratePrivateKey());
            string publicKey = manifest.Signature!.PublicKey;

            manifest.Signature.Signature = "not valid base64!!!";

            // Must return false, not throw FormatException.
            Assert.False(ManifestSigner.Verify(manifest, publicKey));
        }

        private static Manifest.Manifest CreateTestManifest()
        {
            return new Manifest.Manifest
            {
                Version = "1.0.0",
                BuildId = DateTimeOffset.UtcNow,
                BaseUrl = "http://localhost/",
                Files = new List<ManifestFile>
                {
                    new ManifestFile
                    {
                        Path = "test.txt",
                        Size = 5,
                        FileHash = "sha256:abc123",
                        ChunkSize = 4
                    }
                }
            };
        }

        private static byte[] GeneratePrivateKey()
        {
            byte[] key = new byte[32];
            RandomNumberGenerator.Fill(key);
            return key;
        }
    }
}
