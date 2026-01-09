using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Compression;
using Hina.Core.Manifest;

namespace Hina.Core.Net
{
    // Minimal HTTP client for manifest and chunk retrieval.
    public sealed class HttpChunkClient
    {
        private readonly HttpClient _http;

        public HttpChunkClient(HttpClient http)
        {
            _http = http;
        }

        public async Task<Manifest.Manifest> GetManifestAsync(Uri baseUrl, string channel, CancellationToken ct)
        {
            // Allow per-channel manifest naming.
            string fileName = channel.Equals("stable", StringComparison.OrdinalIgnoreCase)
                ? "manifest.json"
                : $"manifest.{channel}.json";

            Uri manifestUrl = new Uri(baseUrl, fileName);
            using (Stream stream = await _http.GetStreamAsync(manifestUrl, ct))
            {
                Manifest.Manifest? manifest = await System.Text.Json.JsonSerializer.DeserializeAsync<Manifest.Manifest>(stream, cancellationToken: ct);
                return manifest ?? new Manifest.Manifest();
            }
        }

        public async Task<byte[]> GetChunkAsync(Uri baseUrl, string strongHash, CancellationToken ct)
        {
            // Chunk URL uses a two-character bucket based on hash prefix.
            string hashOnly = HashOnly(strongHash);
            string bucket = hashOnly.Substring(0, 2);
            string relative = $"chunks/{bucket}/{hashOnly}.chunk.br";
            Uri chunkUrl = new Uri(baseUrl, relative);
            byte[] compressed = await _http.GetByteArrayAsync(chunkUrl, ct);
            return BrotliCodec.Decompress(compressed);
        }

        private static string HashOnly(string strongHash)
        {
            int idx = strongHash.IndexOf(':');
            return idx >= 0 ? strongHash.Substring(idx + 1) : strongHash;
        }
    }
}
