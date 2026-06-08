using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hina.PackageManager.Registry
{
    // Thrown when the on-disk registry was written by a newer schema than this Hina supports.
    public sealed class RegistrySchemaException : Exception
    {
        public RegistrySchemaException(string message) : base(message) { }
    }

    // Reads and writes registry.json. Writes are atomic (tmp + fsync + rename) so a crash
    // mid-write leaves the previous good registry in place. Caller must hold a LockManager lock.
    public sealed class RegistryStore
    {
        private readonly string _path;
        private readonly ILogger _logger;

        public RegistryStore(string path, ILogger? logger = null)
        {
            _path = path;
            _logger = logger ?? NullLogger.Instance;
        }

        public Registry Load()
        {
            if (!File.Exists(_path))
            {
                return new Registry();
            }

            return Parse(File.ReadAllText(_path));
        }

        // Async counterpart of Load() for the async call paths (read-only commands, services)
        // so registry reads don't block a thread on file IO. Same schema gate as Load().
        public async Task<Registry> LoadAsync(CancellationToken ct = default)
        {
            if (!File.Exists(_path))
            {
                return new Registry();
            }

            return Parse(await File.ReadAllTextAsync(_path, ct));
        }

        private Registry Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                // M1: file exists but is empty — likely a partial write or truncation.
                // Don't silently pretend nothing is installed; surface a warning so the
                // user can spot the corruption and run `hina verify --repair`.
                _logger.LogWarning(
                    "Registry at {Path} is empty; treating as new. If you expected installed apps to be listed, run `hina verify` to inspect on-disk state.",
                    _path);
                return new Registry();
            }

            return LoadFromJson(json, Registry.CurrentSchemaVersion, RegistryMigrator.Default, _logger, _path);
        }

        // Deserialize a registry document, upgrading an OLDER schema forward through the
        // migration chain first. A NEWER schema is still refused (source-gen silently drops
        // fields it doesn't know, so loading-then-saving would truncate the newer data — the
        // user must upgrade Hina). Internal so tests can drive the migration path with a
        // synthetic target+migrations while the production schema is still version 1.
        internal static Registry LoadFromJson(string json, int targetVersion, IReadOnlyList<RegistryMigration> migrations, ILogger logger, string path)
        {
            JsonNode? node = JsonNode.Parse(json);
            if (node is JsonObject root)
            {
                int version = ReadSchemaVersion(root);
                if (version > targetVersion)
                {
                    throw NewerSchema(path, version, targetVersion);
                }
                if (version < targetVersion)
                {
                    RegistryMigrator.Migrate(root, targetVersion, migrations);
                }
                return root.Deserialize(PackageManagerJsonContext.Default.Registry) ?? new Registry();
            }

            // Non-object / "null" JSON: defer to the source-gen deserializer (preserves the
            // prior behavior, including the higher-schema gate).
            Registry r = JsonSerializer.Deserialize(json, PackageManagerJsonContext.Default.Registry) ?? new Registry();
            if (r.SchemaVersion > targetVersion)
            {
                throw NewerSchema(path, r.SchemaVersion, targetVersion);
            }
            return r;
        }

        private static int ReadSchemaVersion(JsonObject root)
        {
            if (root.TryGetPropertyValue("schemaVersion", out JsonNode? node) && node is not null)
            {
                try { return node.GetValue<int>(); }
                catch (Exception) { return 1; }
            }
            return 1;
        }

        private static RegistrySchemaException NewerSchema(string path, int found, int max) =>
            new RegistrySchemaException(
                $"Registry at '{path}' has schema version {found}, but this Hina only understands up to {max}. " +
                "Upgrade Hina to manage this registry; an older Hina would corrupt it.");

        public async Task SaveAsync(Registry registry, CancellationToken ct = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");

            string tmp = _path + ".tmp";

            using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(fs, registry, PackageManagerIndentedJsonContext.Default.Registry, ct);
                await fs.FlushAsync(ct);
                fs.Flush(flushToDisk: true);
            }

            // Atomic replace. File.Move with overwrite is atomic on POSIX and Windows for same-volume renames.
            File.Move(tmp, _path, overwrite: true);
        }
    }
}
