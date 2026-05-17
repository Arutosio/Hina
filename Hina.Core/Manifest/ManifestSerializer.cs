using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Json;

namespace Hina.Core.Manifest
{
    // Small JSON helper to keep manifest I/O consistent.
    public static class ManifestSerializer
    {
        public static async Task WriteAsync(Manifest manifest, string path, CancellationToken ct)
        {
            using (FileStream fs = File.Create(path))
            {
                await JsonSerializer.SerializeAsync(fs, manifest, HinaCoreIndentedJsonContext.Default.Manifest, ct);
            }
        }

        public static async Task<Manifest> ReadAsync(string path, CancellationToken ct)
        {
            using (FileStream fs = File.OpenRead(path))
            {
                Manifest? manifest = await JsonSerializer.DeserializeAsync(fs, HinaCoreJsonContext.Default.Manifest, ct);
                return manifest ?? new Manifest();
            }
        }
    }
}
