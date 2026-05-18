using System;
using NSec.Cryptography;

namespace Hina.PackageManager.Descriptor
{
    // Ed25519 sign/verify over the canonical bytes of an AppDescriptor (descriptor with
    // DescriptorSignature stripped). Mirrors Hina.Core's ManifestSigner so publishers can
    // reuse the same key pair for both layers.
    public static class DescriptorSigner
    {
        private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;

        // Returns the signed descriptor (caller can re-serialize and ship it). Mutates input.
        public static AppDescriptor AttachSignature(AppDescriptor descriptor, byte[] privateKeyBytes)
        {
            byte[] data = DescriptorParser.GetCanonicalBytes(descriptor);

            using Key key = Key.Import(Algorithm, privateKeyBytes, KeyBlobFormat.RawPrivateKey);
            byte[] signature = Algorithm.Sign(key, data);
            byte[] publicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);

            descriptor.DescriptorSignature = new DescriptorSignature
            {
                Algorithm = "ed25519",
                Signature = Convert.ToBase64String(signature),
                PublicKey = Convert.ToBase64String(publicKey)
            };
            return descriptor;
        }

        // Returns true if the descriptor was signed by trustedPublicKeyBase64.
        // Caller decides whether trustedPublicKey is the descriptor's own (TOFU on first install)
        // or the registry-pinned key (every subsequent update).
        public static bool Verify(AppDescriptor descriptor, string trustedPublicKeyBase64)
        {
            if (descriptor.DescriptorSignature == null)
            {
                return false;
            }

            byte[] data = DescriptorParser.GetCanonicalBytes(descriptor);
            byte[] signature;
            byte[] publicKey;
            try
            {
                signature = Convert.FromBase64String(descriptor.DescriptorSignature.Signature);
                publicKey = Convert.FromBase64String(trustedPublicKeyBase64);
            }
            catch (FormatException)
            {
                return false;
            }

            if (publicKey.Length != 32)
            {
                return false;
            }

            PublicKey key = PublicKey.Import(Algorithm, publicKey, KeyBlobFormat.RawPublicKey);
            return Algorithm.Verify(key, data, signature);
        }
    }
}
