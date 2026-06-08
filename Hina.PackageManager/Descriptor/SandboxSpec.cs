using System.Collections.Generic;

namespace Hina.PackageManager.Descriptor
{
    // Optional Flatpak-style filesystem isolation block in hina.app.json.
    // Absent or Enabled=false ⇒ the app launches unsandboxed (legacy behavior).
    // When Enabled, the app sees only the install dir plus the paths listed here
    // (and any extra paths the user grants at runtime). Linux-only enforcement in
    // v1 (Landlock); other OSes degrade to no-op. Filesystem-only — network and
    // devices are not restricted yet.
    public sealed class SandboxSpec
    {
        public bool Enabled { get; set; }

        public List<FsRule> Filesystem { get; set; } = new List<FsRule>();

        // Declared non-filesystem capabilities (network, audio, …). Not enforced
        // in v1 — shown to the user as declared intent. Null ⇒ none declared.
        public CapabilitySpec? Capabilities { get; set; }
    }

    // A requested filesystem grant. Path is an abstract token (never a raw host
    // path) so the same descriptor resolves correctly across machines/users.
    public sealed class FsRule
    {
        // One of SandboxTokens.Known. "host" means no filesystem restriction.
        public string Path { get; set; } = string.Empty;

        // "ro" (read-only) or "rw" (read-write). Exec is implied for the app dir only.
        public string Access { get; set; } = "ro";
    }
}
