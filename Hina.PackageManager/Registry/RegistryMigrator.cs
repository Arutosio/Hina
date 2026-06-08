using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Hina.PackageManager.Registry
{
    // One step that upgrades a registry JSON document from schema FromVersion to
    // FromVersion+1, mutating the raw JsonObject in place. Migrations work on the raw
    // JSON (not a deserialized Registry) because the source-generated serializer
    // silently drops fields it doesn't know — by the time you have a Registry object
    // the older/newer data a migration needs may already be gone.
    public sealed class RegistryMigration
    {
        public int FromVersion { get; }
        public Action<JsonObject> Apply { get; }

        public RegistryMigration(int fromVersion, Action<JsonObject> apply)
        {
            FromVersion = fromVersion;
            Apply = apply;
        }
    }

    // Upgrades an on-disk registry document to the current schema by applying ordered
    // migrations. This is the upgrade path that replaces "refuse any non-current
    // schema": a registry written by an OLDER Hina is migrated forward; one written by
    // a NEWER Hina is still refused by RegistryStore (we can't know its future fields).
    //
    // Default is empty while the schema is still version 1 — it is the scaffold for the
    // first non-additive bump. To add a migration: bump Registry.CurrentSchemaVersion
    // and append a RegistryMigration(oldVersion, transform) to Default.
    public static class RegistryMigrator
    {
        public static readonly IReadOnlyList<RegistryMigration> Default = Array.Empty<RegistryMigration>();

        public static JsonObject Migrate(JsonObject root, int targetVersion, IReadOnlyList<RegistryMigration> migrations)
        {
            int version = ReadVersion(root);
            while (version < targetVersion)
            {
                RegistryMigration? step = null;
                foreach (RegistryMigration m in migrations)
                {
                    if (m.FromVersion == version) { step = m; break; }
                }
                if (step == null)
                {
                    throw new RegistrySchemaException(
                        $"No registry migration from schema version {version} to {version + 1}; cannot upgrade to {targetVersion}.");
                }

                step.Apply(root);
                version++;
                root["schemaVersion"] = version;
            }
            return root;
        }

        // A registry that predates the schemaVersion field is treated as the first
        // version (1) — the oldest format that could lack it.
        private static int ReadVersion(JsonObject root)
        {
            if (root.TryGetPropertyValue("schemaVersion", out JsonNode? node) && node is not null)
            {
                try { return node.GetValue<int>(); }
                catch (Exception) { return 1; }
            }
            return 1;
        }
    }
}
