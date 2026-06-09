using System;
using System.Collections.Generic;

namespace Hina.PackageManager.Registry
{
    // On-disk index of every app Hina has installed. One row per app.
    public sealed class Registry
    {
        // The schema this build of Hina reads and writes. Bump when the on-disk shape changes
        // in a way an older Hina can't round-trip. RegistryStore refuses to load (and thus
        // can't overwrite) a registry stamped with a higher version — see RegistryStore.Load.
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
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

        // Variant token installed (e.g. "macos-arm64") for a multi-platform app, so update/verify
        // refetch the same manifest.<token>.json. Empty for legacy single-manifest apps. Additive
        // and default-empty so older registries round-trip unchanged.
        public string Platform { get; set; } = string.Empty;

        public DateTimeOffset InstalledAt { get; set; }
        public DateTimeOffset LastUpdatedAt { get; set; }

        // Side-effects that were created on disk. Read at uninstall and update
        // time because the live descriptor cannot be trusted to still list the
        // same hooks/entries.
        public List<HookEvidence> ExecutedHooks { get; set; } = new List<HookEvidence>();
        public List<ShellEntryRecord> ShellEntries { get; set; } = new List<ShellEntryRecord>();

        // Extra filesystem paths the user has granted this app at runtime, beyond
        // what the descriptor's sandbox block declared. Absolute resolved paths.
        // Additive and default-empty so older registries round-trip unchanged.
        public List<FsGrant> UserGrants { get; set; } = new List<FsGrant>();
    }

    // A user-granted absolute filesystem path for a sandboxed app.
    public sealed class FsGrant
    {
        public string Path { get; set; } = string.Empty;
        // "ro" or "rw".
        public string Access { get; set; } = "ro";
    }

    public sealed class HookEvidence
    {
        public string Action { get; set; } = string.Empty;
        // Stable identity computed from the descriptor at apply time; used by UpdateService
        // to diff against the new descriptor's hooks. Optional for backwards-compat with
        // pre-Phase-3 registries.
        public string Identity { get; set; } = string.Empty;
        public string Evidence { get; set; } = string.Empty;
    }

    public sealed class ShellEntryRecord
    {
        public string Id { get; set; } = string.Empty;
        public string Evidence { get; set; } = string.Empty;
    }
}
