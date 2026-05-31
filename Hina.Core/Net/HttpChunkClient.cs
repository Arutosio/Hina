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
        // A manifest is JSON metadata, not payload — a few MB even for huge apps. Cap the download
        // so a hostile/compromised server can't stream an unbounded body into the JSON parser
        // (the signature is only checked AFTER deserialize). Mirrors DescriptorFetcher's 5 MB cap.
        public const long MaxManifestBytes = 32 * 1024 * 1024;

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
                return await _retryPolicy.ExecuteAsync(token => FetchManifestOnceAsync(manifestUrl, token), ct);
            }
            return await FetchManifestOnceAsync(manifestUrl, ct);
        }

        private async Task<Manifest.Manifest> FetchManifestOnceAsync(Uri manifestUrl, CancellationToken ct)
        {
            using HttpResponseMessage response = await _http.GetAsync(manifestUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            // ResponseHeadersRead returns as soon as headers arrive, before observing a token that
            // was cancelled mid-request; honour cancellation before treating a status as a retryable error.
            ct.ThrowIfCancellationRequested();
            EnsureNoDowngrade(manifestUrl, response);
            response.EnsureSuccessStatusCode();
            EnsureWithinCap(response, MaxManifestBytes, "Manifest", manifestUrl);

            using Stream stream = await response.Content.ReadAsStreamAsync(ct);
            using CappedReadStream limited = new CappedReadStream(stream, MaxManifestBytes);
            Manifest.Manifest? manifest = await JsonSerializer.DeserializeAsync(limited, HinaCoreJsonContext.Default.Manifest, ct);
            return manifest ?? new Manifest.Manifest();
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
            // Also bound the *compressed* download: GetByteArrayAsync would otherwise buffer an
            // unbounded body and OOM the client before decompression. Brotli rarely expands, but
            // allow generous slack so legitimate near-incompressible chunks still fit.
            long compressedCap = maxBytes + (64 * 1024);

            if (_retryPolicy != null)
            {
                return await _retryPolicy.ExecuteAsync(token => FetchChunkOnceAsync(chunkUrl, hashOnly, maxBytes, compressedCap, token), ct);
            }
            return await FetchChunkOnceAsync(chunkUrl, hashOnly, maxBytes, compressedCap, ct);
        }

        private async Task<byte[]> FetchChunkOnceAsync(Uri chunkUrl, string hashOnly, long maxBytes, long compressedCap, CancellationToken ct)
        {
            using HttpResponseMessage response = await _http.GetAsync(chunkUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            ct.ThrowIfCancellationRequested();
            EnsureNoDowngrade(chunkUrl, response);
            response.EnsureSuccessStatusCode();
            EnsureWithinCap(response, compressedCap, "Chunk", chunkUrl);

            using Stream stream = await response.Content.ReadAsStreamAsync(ct);
            using CappedReadStream limited = new CappedReadStream(stream, compressedCap);
            using MemoryStream buffer = new MemoryStream();
            await limited.CopyToAsync(buffer, ct);
            return DecompressAndVerify(buffer.ToArray(), hashOnly, maxBytes);
        }

        // A redirect that lands on http:// after we requested https:// pulls the payload over a
        // tamperable channel. The chunk hash / manifest signature still catch tampering, but we
        // refuse the downgrade for parity with DescriptorFetcher. Starting on http (only via
        // --allow-insecure upstream) is unaffected.
        private static void EnsureNoDowngrade(Uri requested, HttpResponseMessage response)
        {
            Uri finalUrl = response.RequestMessage?.RequestUri ?? requested;
            if (string.Equals(requested.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(finalUrl.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Refusing fetch: '{requested}' redirected to insecure '{finalUrl}' (HTTPS→HTTP downgrade).");
            }
        }

        private static void EnsureWithinCap(HttpResponseMessage response, long cap, string what, Uri url)
        {
            if (response.Content.Headers.ContentLength is long cl && cl > cap)
            {
                throw new InvalidDataException($"{what} at {url} reports {cl} bytes (max allowed {cap}).");
            }
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

        // Read-side cap: throws once more than `max` bytes have been read, so a server that omits
        // or lies about Content-Length still can't stream an unbounded body.
        private sealed class CappedReadStream : Stream
        {
            private readonly Stream _inner;
            private readonly long _max;
            private long _read;

            public CappedReadStream(Stream inner, long max) { _inner = inner; _max = max; }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int n = _inner.Read(buffer, offset, count);
                Account(n);
                return n;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                int n = await _inner.ReadAsync(buffer, cancellationToken);
                Account(n);
                return n;
            }

            private void Account(int n)
            {
                _read += n;
                if (_read > _max)
                {
                    throw new InvalidDataException($"Response exceeded {_max} bytes.");
                }
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => _read; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
