namespace Hina.Core.Manifest
{
    // Signature metadata for manifest integrity and authenticity.
    public sealed class ManifestSignature
    {
        public string Algorithm { get; set; } = "ed25519";
        public string Signature { get; set; } = string.Empty;
        public string PublicKey { get; set; } = string.Empty;
    }
}
