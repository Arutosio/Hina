using System;
using System.Collections.Generic;

namespace Hina.PackageManager.Registry
{
    // On-disk index of every app Hina has installed. One row per app.
    public sealed class Registry
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, InstalledApp> Apps { get; set; } = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class InstalledApp
    {
        public string Name { get; set; } = string.Empty;
        public string InstalledVersion { get; set; } = string.Empty;
        public string InstallPath { get; set; } = string.Empty;
        public string DescriptorUrl { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = string.Empty;
        public string Channel { get; set; } = "stable";
        public string PublicKey { get; set; } = string.Empty;
        public DateTimeOffset InstalledAt { get; set; }
        public DateTimeOffset LastUpdatedAt { get; set; }

        // Side-effects that were created on disk. Read at uninstall time
        // because the live descriptor cannot be trusted to still list the
        // same hooks/entries.
        public List<HookEvidence> ExecutedHooks { get; set; } = new List<HookEvidence>();
        public List<string> ShellEntries { get; set; } = new List<string>();
    }

    public sealed class HookEvidence
    {
        public string Action { get; set; } = string.Empty;
        public string Evidence { get; set; } = string.Empty;
    }
}
