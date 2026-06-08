namespace Hina.PackageManager.Descriptor
{
    // Non-filesystem capabilities an app declares in its signed sandbox block.
    // v1 does NOT enforce any of these — only filesystem scope is enforced
    // (Linux/Landlock). They are declared intent, surfaced to the user by
    // `hina perms` with a "declared — not enforced" marker. Enforcement (portals,
    // PipeWire, Wayland, per-OS device policy) is future work.
    public sealed class CapabilitySpec
    {
        public bool Network { get; set; }
        public bool Audio { get; set; }
        public bool Microphone { get; set; }
        public bool Screen { get; set; }
        public bool Input { get; set; }
        public bool Devices { get; set; }
    }
}
