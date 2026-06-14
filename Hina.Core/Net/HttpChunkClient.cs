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
    public sealed class HttpChunkClient : IDisposable
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
            => await GetManifestAsync(baseUrl, channel, platform: null, ct);

        public async Task<Manifest.Manifest> GetManifestAsync(Uri baseUrl, string channel, string? platform, CancellationToken ct)
        {
            // Manifest naming: manifest[.<channel>][.<platform>].json. The channel segment is
            // dropped for "stable"; the platform segment is present only for multi-variant apps,
            // so a legacy single-manifest app on the stable channel stays "manifest.json".
            string fileName = "manifest";
            if (!channel.Equals("stable", StringComparison.OrdinalIgnoreCase))
            {
                fileName += $".{channel}";
            }
            if (!string.IsNullOrEmpty(platform))
            {
                fileName += $".{platform}";
            }
            fileName += ".json";

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
            ThrowWithRetryAfterIfRateLimited(response);
            response.EnsureSuccessStatusCode();
            EnsureWithinCap(response, MaxManifestBytes, "Manifest", manifestUrl);

            using Stream stream = await response.Content.ReadAsStreamAsync(ct);
            using CappedReadStream limited = new CappedReadStream(stream, MaxManifestBytes);
            try
            {
                Manifest.Manifest? manifest = await JsonSerializer.DeserializeAsync(limited, HinaCoreJsonContext.Default.Manifest, ct);
                return manifest ?? new Manifest.Manifest();
            }
            catch (JsonException ex)
            {
                // A proxy login page, captive portal, or an HTML 404 served as 200 lands here.
                // Mirror the descriptor fetcher: name the URL instead of leaking the raw
                // parser error ("'<' is an invalid start of a value").
                throw new InvalidDataException(
                    $"The server at {manifestUrl} did not return a valid manifest (JSON parse failed: {ex.Message}) " +
                    "Check that the base URL points at a Hina patch root and that no proxy/captive portal is intercepting the request.");
            }
        }

        public async Task<byte[]> GetChunkAsync(Uri baseUrl, string strongHash, CancellationToken ct, int expectedSize = 0)
        {
            // Chunk URL uses a two-character bucket based on hash prefix. A hostile/corrupt
            // manifest can carry a hash shorter than that; fail as a manifest error instead
            // of an ArgumentOutOfRangeException from Substring.
            string hashOnly = HashOnly(strongHash);
            if (hashOnly.Length < 2)
            {
                throw new InvalidDataException($"Manifest contains an invalid chunk strong hash '{strongHash}'.");
            }
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
            ThrowWithRetryAfterIfRateLimited(response);
            response.EnsureSuccessStatusCode();
            EnsureWithinCap(response, compressedCap, "Chunk", chunkUrl);

            using Stream stream = await response.Content.ReadAsStreamAsync(ct);
            using CappedReadStream limited = new CappedReadStream(stream, compressedCap);
            // Pre-size from Content-Length when the server provides one (EnsureWithinCap already
            // rejected anything above the cap), and read the backing buffer directly instead of
            // ToArray() — skips one full copy of every chunk. The int.MaxValue guard keeps a
            // hostile header that passed the cap from overflowing the cast.
            int capacity = response.Content.Headers.ContentLength is long cl && cl > 0 && cl <= int.MaxValue ? (int)cl : 0;
            using MemoryStream buffer = new MemoryStream(capacity);
            await limited.CopyToAsync(buffer, ct);
            return DecompressAndVerify(buffer.GetBuffer(), (int)buffer.Length, hashOnly, maxBytes);
        }

        // When the server returns 429 Too Many Requests, read the Retry-After header (if present)
        // and throw an HttpRequestException that carries the hint in Exception.Data so that
        // RetryPolicy.ExecuteAsync can honour it instead of computing an exponential backoff delay.
        // We must do this *before* EnsureSuccessStatusCode because the response is disposed after
        // that call and the headers become inaccessible.
        private static void ThrowWithRetryAfterIfRateLimited(HttpResponseMessage response)
        {
            if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
            {
                return;
            }

            var ex = new HttpRequestException(
                $"Server returned 429 Too Many Requests.",
                inner: null,
                statusCode: System.Net.HttpStatusCode.TooManyRequests);

            // Retry-After can be a delta-seconds integer or an HTTP-date; only the integer form
            // is useful here. Ignore unparseable values — the retry policy will fall back to
            // exponential backoff automatically.
            if (response.Headers.RetryAfter?.Delta is TimeSpan delta && delta.TotalSeconds > 0)
            {
                // Convert to milliseconds and clamp to int range; RetryPolicy caps it further at maxDelayMs.
                long retryAfterMs = (long)delta.TotalMilliseconds;
                ex.Data[RetryPolicy.RetryAfterMsDataKey] = retryAfterMs > int.MaxValue ? int.MaxValue : (int)retryAfterMs;
            }

            throw ex;
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
        // whole-file verification is disabled. Integrity failures throw ChunkIntegrityException,
        // which RetryPolicy treats as transient: one corrupt CDN edge object shouldn't kill the
        // whole install when a re-fetch can hit a healthy node. The decompression-bomb cap stays
        // a plain InvalidDataException — deterministic hostility, retrying it is pointless.
        private static byte[] DecompressAndVerify(byte[] compressed, int compressedLength, string expectedHashOnly, long maxBytes)
        {
            byte[] data;
            try
            {
                data = BrotliCodec.Decompress(compressed, 0, compressedLength, maxBytes);
            }
            catch (InvalidOperationException ex)
            {
                // BrotliStream reports malformed input as InvalidOperationException; surface it
                // as an actionable integrity error instead of a raw runtime exception.
                throw new ChunkIntegrityException(
                    $"Chunk {expectedHashOnly} is not valid Brotli data. The chunk store returned corrupt or truncated content.", ex);
            }

            string actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data));
            if (!string.Equals(actual, expectedHashOnly, StringComparison.OrdinalIgnoreCase))
            {
                throw new ChunkIntegrityException(
                    $"Chunk content hash mismatch: expected {expectedHashOnly}, got {actual}. The chunk store returned corrupt or tampered data.");
            }
            return data;
        }

        private static string HashOnly(string strongHash)
        {
            int idx = strongHash.IndexOf(':');
            return idx >= 0 ? strongHash.Substring(idx + 1) : strongHash;
        }

        public void Dispose() => _http.Dispose();

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
