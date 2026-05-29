using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Compression;
using Hina.Core.Json;
using Hina.Core.Manifest;
using Microsoft.Extensions.Logging;

namespace Hina.Core.Net
{
    // Minimal HTTP client for manifest and chunk retrieval.
    public sealed class HttpChunkClient
    {
        private readonly HttpClient _http;
        private readonly RetryPolicy? _retryPolicy;

        public HttpChunkClient(HttpClient http)
        {
            _http = http;
        }

        public HttpChunkClient(HttpClient http, RetryPolicy retryPolicy)
        {
            _http = http;
            _retryPolicy = retryPolicy;
        }

        public async Task<Manifest.Manifest> GetManifestAsync(Uri baseUrl, string channel, CancellationToken ct)
        {
            // Allow per-channel manifest naming.
            string fileName = channel.Equals("stable", StringComparison.OrdinalIgnoreCase)
                ? "manifest.json"
                : $"manifest.{channel}.json";

            Uri manifestUrl = new Uri(baseUrl, fileName);

            if (_retryPolicy != null)
            {
                return await _retryPolicy.ExecuteAsync(async token =>
                {
                    using (Stream stream = await _http.GetStreamAsync(manifestUrl, token))
                    {
                        Manifest.Manifest? manifest = await JsonSerializer.DeserializeAsync(stream, HinaCoreJsonContext.Default.Manifest, token);
                        return manifest ?? new Manifest.Manifest();
                    }
                }, ct);
            }

            using (Stream stream = await _http.GetStreamAsync(manifestUrl, ct))
            {
                Manifest.Manifest? manifest = await JsonSerializer.DeserializeAsync(stream, HinaCoreJsonContext.Default.Manifest, ct);
                return manifest ?? new Manifest.Manifest();
            }
        }

        public async Task<byte[]> GetChunkAsync(Uri baseUrl, string strongHash, CancellationToken ct, int expectedSize = 0)
        {
            // Chunk URL uses a two-character bucket based on hash prefix.
            string hashOnly = HashOnly(strongHash);
            string bucket = hashOnly.Substring(0, 2);
            string relative = $"chunks/{bucket}/{hashOnly}.chunk.br";
            Uri chunkUrl = new Uri(baseUrl, relative);

            // The manifest knows the exact decompressed size; use it as the bomb cap so a
            // hostile server can't expand a tiny chunk into gigabytes. Fall back to the codec
            // default when the caller doesn't know the size.
            long maxBytes = expectedSize > 0 ? expectedSize : BrotliCodec.DefaultMaxDecompressedBytes;

            if (_retryPolicy != null)
            {
                return await _retryPolicy.ExecuteAsync(async token =>
                {
                    byte[] compressed = await _http.GetByteArrayAsync(chunkUrl, token);
                    return DecompressAndVerify(compressed, hashOnly, maxBytes);
                }, ct);
            }

            byte[] compressedData = await _http.GetByteArrayAsync(chunkUrl, ct);
            return DecompressAndVerify(compressedData, hashOnly, maxBytes);
        }

        // Chunks are content-addressed: the URL names the chunk by its SHA-256. Verify the
        // decompressed bytes actually hash to that name, independent of the optional whole-file
        // verify pass (PatcherConfig.Verify) — so a corrupt/tampered chunk is rejected even when
        // whole-file verification is disabled, and the failure is localized for retry.
        private static byte[] DecompressAndVerify(byte[] compressed, string expectedHashOnly, long maxBytes)
        {
            byte[] data = BrotliCodec.Decompress(compressed, maxBytes);

            string actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data));
            if (!string.Equals(actual, expectedHashOnly, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Chunk content hash mismatch: expected {expectedHashOnly}, got {actual}. The chunk store returned corrupt or tampered data.");
            }
            return data;
        }

        private static string HashOnly(string strongHash)
        {
            int idx = strongHash.IndexOf(':');
            return idx >= 0 ? strongHash.Substring(idx + 1) : strongHash;
        }
    }
}
