namespace Hina.PackageManager.Descriptor
{
    // Ed25519 signature over the canonical descriptor bytes (descriptor with this field nulled).
    public sealed class DescriptorSignature
    {
        public string Algorithm { get; set; } = "ed25519";
        public string Signature { get; set; } = string.Empty;
        public string PublicKey { get; set; } = string.Empty;
    }
}
