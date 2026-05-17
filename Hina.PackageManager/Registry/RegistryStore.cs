using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Json;

namespace Hina.PackageManager.Registry
{
    // Reads and writes registry.json. Writes are atomic (tmp + fsync + rename) so a crash
    // mid-write leaves the previous good registry in place. Caller must hold a LockManager lock.
    public sealed class RegistryStore
    {
        private readonly string _path;

        public RegistryStore(string path)
        {
            _path = path;
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
