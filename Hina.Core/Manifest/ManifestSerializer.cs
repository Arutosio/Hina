using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hina.Core.Manifest
{
    // Small JSON helper to keep manifest I/O consistent.
    public static class ManifestSerializer
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public static async Task WriteAsync(Manifest manifest, string path, CancellationToken ct)
        {
            using (FileStream fs = File.Create(path))
            {
                await JsonSerializer.SerializeAsync(fs, manifest, Options, ct);
            }
        }

        public static async Task<Manifest> ReadAsync(string path, CancellationToken ct)
        {
            using (FileStream fs = File.OpenRead(path))
            {
                Manifest? manifest = await JsonSerializer.DeserializeAsync<Manifest>(fs, Options, ct);
                return manifest ?? new Manifest();
            }
        }
    }
}
