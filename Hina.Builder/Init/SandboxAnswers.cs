using Hina.PackageManager.Descriptor;

namespace Hina.Builder.Init
{
    // Where the app is allowed to write, expressed in plain language. Maps to sandbox fs tokens.
    internal enum DataLocation
    {
        SelfOnly,   // only its own install dir (implicit) — no extra grant
        Config,     // xdg-config
        Documents,  // xdg-documents
        Anywhere    // host (escape hatch)
    }

    // The handful of human answers the wizard collects about isolation, plus the translation
    // into a SandboxSpec. Pure: no I/O, fully unit-testable.
    internal sealed class SandboxAnswers
    {
        public bool Enabled { get; init; } = true;
        public bool NeedsNetwork { get; init; }
        public DataLocation DataLocation { get; init; } = DataLocation.SelfOnly;

        // null when sandbox disabled (descriptor.Sandbox stays null ⇒ unsandboxed).
        public SandboxSpec? ToSpec()
        {
            if (!Enabled)
            {
                return null;
            }

            SandboxSpec spec = new SandboxSpec { Enabled = true };

            switch (DataLocation)
            {
                case DataLocation.Config:
                    spec.Filesystem.Add(new FsRule { Path = SandboxTokens.XdgConfig, Access = "rw" });
                    break;
                case DataLocation.Documents:
                    spec.Filesystem.Add(new FsRule { Path = SandboxTokens.XdgDocuments, Access = "rw" });
                    break;
                case DataLocation.Anywhere:
                    spec.Filesystem.Add(new FsRule { Path = SandboxTokens.Host, Access = "rw" });
                    break;
                case DataLocation.SelfOnly:
                default:
                    // App install dir is always implicitly granted ro+exec; nothing to add.
                    break;
            }

            if (NeedsNetwork)
            {
                spec.Capabilities = new CapabilitySpec { Network = true };
            }

            return spec;
        }
    }
}
