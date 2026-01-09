using System;
using NSec.Cryptography;

namespace Hina.Core.Crypto
{
    // Generates Ed25519 key pairs for manifest signing.
    public static class KeyGenerator
    {
        public static (string PrivateKeyBase64, string PublicKeyBase64) GenerateEd25519()
        {
            var algorithm = SignatureAlgorithm.Ed25519;
            var parameters = new KeyCreationParameters
            {
                ExportPolicy = KeyExportPolicies.AllowPlaintextExport
            };
            using (var key = Key.Create(algorithm, parameters))
            {
                byte[] privateKey = key.Export(KeyBlobFormat.RawPrivateKey);
                byte[] publicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
                return (Convert.ToBase64String(privateKey), Convert.ToBase64String(publicKey));
            }
        }
    }
}
