using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hina.PackageManager.Registry
{
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

            string json = File.ReadAllText(_path);
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

            Registry? r = JsonSerializer.Deserialize(json, PackageManagerJsonContext.Default.Registry);
            return r ?? new Registry();
        }

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
