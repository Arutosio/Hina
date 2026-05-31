using System;
using System.Text.Json;
using Hina.Core.Json;
using NSec.Cryptography;

namespace Hina.Core.Manifest
{
    // Signs and verifies manifest integrity with Ed25519.
    public static class ManifestSigner
    {
        private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;

        public static void AttachSignature(Manifest manifest, byte[] privateKeyBytes)
        {
            byte[] data = GetCanonicalBytes(manifest);
            using (Key key = Key.Import(Algorithm, privateKeyBytes, KeyBlobFormat.RawPrivateKey))
            {
                byte[] signature = Algorithm.Sign(key, data);
                byte[] publicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
                manifest.Signature = new ManifestSignature
                {
                    Algorithm = "ed25519",
                    Signature = Convert.ToBase64String(signature),
                    PublicKey = Convert.ToBase64String(publicKey)
                };
            }
        }

        public static bool Verify(Manifest manifest, string trustedPublicKeyBase64)
        {
            if (manifest.Signature == null)
            {
                return false;
            }

            // Pin the algorithm: only ed25519 is implemented, so any other value is a downgrade/
            // confusion attempt, not a real alternative. Mirrors DescriptorSigner.Verify.
            if (!string.Equals(manifest.Signature.Algorithm, "ed25519", StringComparison.Ordinal))
            {
                return false;
            }

            byte[] data = GetCanonicalBytes(manifest);
            byte[] signature;
            byte[] publicKey;
            try
            {
                signature = Convert.FromBase64String(manifest.Signature.Signature);
                publicKey = Convert.FromBase64String(trustedPublicKeyBase64);
            }
            catch (FormatException)
            {
                // Malformed base64 is a failed verification, not an exception to bubble up.
                return false;
            }
            if (publicKey.Length != 32)
            {
                return false;
            }

            PublicKey key = PublicKey.Import(Algorithm, publicKey, KeyBlobFormat.RawPublicKey);
            return Algorithm.Verify(key, data, signature);
        }

        private static byte[] GetCanonicalBytes(Manifest manifest)
        {
            // Serialize without signature to keep the payload stable.
            Manifest unsigned = new Manifest
            {
                Version = manifest.Version,
                BuildId = manifest.BuildId,
                BaseUrl = manifest.BaseUrl,
                Files = manifest.Files,
                Signature = null
            };

            return JsonSerializer.SerializeToUtf8Bytes(unsigned, HinaCoreCanonicalJsonContext.Default.Manifest);
        }
    }
}
