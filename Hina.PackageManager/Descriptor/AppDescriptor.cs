using System.Collections.Generic;

namespace Hina.PackageManager.Descriptor
{
    // Wire format for the `hina.app.json` file a publisher hosts at the install URL.
    public sealed class AppDescriptor
    {
        public int SchemaVersion { get; set; } = 1;

        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Publisher { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string? Homepage { get; set; }
        public string? License { get; set; }
        public string? Icon { get; set; }
        public string? MinHinaVersion { get; set; }

        public string BaseUrl { get; set; } = string.Empty;
        public string Channel { get; set; } = "stable";
        public string PublicKey { get; set; } = string.Empty;

        public ExecMap Exec { get; set; } = new ExecMap();

        public List<ShellEntry> Entries { get; set; } = new List<ShellEntry>();
        public List<HookAction> PostInstall { get; set; } = new List<HookAction>();

        public DescriptorSignature? DescriptorSignature { get; set; }
    }
}
