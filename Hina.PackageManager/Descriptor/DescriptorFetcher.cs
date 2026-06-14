using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Net;

namespace Hina.PackageManager.Descriptor
{
    // Fetches a hina.app.json descriptor over HTTP. Capped at 5 MB to fail fast
    // against hostile/misconfigured servers serving giant payloads. Retries on
    // transient failures (5xx / network) with exponential backoff so a flaky CDN
    // doesn't break `hina install` on the first try.
    public class DescriptorFetcher
    {
        public const long MaxDescriptorBytes = 5 * 1024 * 1024;
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
        public const int DefaultMaxRetries = 5;
        public static readonly TimeSpan DefaultRetryBaseDelay = TimeSpan.FromMilliseconds(500);
        public static readonly TimeSpan DefaultMaxRetryDelay = TimeSpan.FromSeconds(15);

        private readonly HttpClient _http;
        private readonly int _maxRetries;
        private readonly TimeSpan _retryBaseDelay;

        private readonly TimeSpan _maxRetryDelay;

        public DescriptorFetcher(HttpClient? http = null, int maxRetries = DefaultMaxRetries, TimeSpan? retryBaseDelay = null, TimeSpan? maxRetryDelay = null)
        {
            _http = http ?? SharedHttp.Instance;
            _maxRetries = Math.Max(1, maxRetries);
            _retryBaseDelay = retryBaseDelay ?? DefaultRetryBaseDelay;
            _maxRetryDelay = maxRetryDelay ?? DefaultMaxRetryDelay;
        }

        public virtual async Task<AppDescriptor> FetchAsync(Uri url, CancellationToken ct)
        {
            // Reject schemes that aren't network-fetchable. HttpClient throws an obscure
            // PlatformNotSupportedException or NotSupportedException on file:// /
            // ftp:// etc.; a clear error up front beats that.
            if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException(
                    $"Descriptor URL must use http:// or https://; got '{url.Scheme}'.");
            }

            Exception? lastError = null;
            for (int attempt = 0; attempt < _maxRetries; attempt++)
            {
                if (attempt > 0)
                {
                    // Exponential backoff capped at _maxRetryDelay so we don't sleep
                    // for hours on retry 10+ over a flaky connection.
                    double rawMs = _retryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
                    TimeSpan delay = TimeSpan.FromMilliseconds(Math.Min(rawMs, _maxRetryDelay.TotalMilliseconds));
                    await Task.Delay(delay, ct);
                }

                try
                {
                    return await FetchOnceAsync(url, ct);
                }
                catch (HttpRequestException ex) when (IsTransient(ex))
                {
                    lastError = ex;
                }
                catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    // Per-request timeout (HttpClient cancels its own token). User-driven
                    // cancellation falls through unwrapped.
                    lastError = ex;
                }
            }
            throw new HttpRequestException(
                $"Descriptor fetch failed after {_maxRetries} attempts: {lastError?.Message}", lastError);
        }

        private async Task<AppDescriptor> FetchOnceAsync(Uri url, CancellationToken ct)
        {
            using HttpResponseMessage response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

            // Guard against a transport downgrade on redirect: if we started on https but the
            // final URL (after any redirects) is http, the descriptor — and the TOFU key it
            // carries on first install — would arrive over a tamperable channel. A legitimate
            // https endpoint never needs to redirect to http, so refuse. Starting on http
            // (only reachable via --allow-insecure upstream) and staying http is unaffected.
            Uri finalUrl = response.RequestMessage?.RequestUri ?? url;
            if (string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(finalUrl.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Refusing descriptor fetch: '{url}' redirected to insecure '{finalUrl}' (HTTPS→HTTP downgrade).");
            }

            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is long cl && cl > MaxDescriptorBytes)
            {
                throw new InvalidDataException(
                    $"Descriptor at {url} reports {cl} bytes (max allowed {MaxDescriptorBytes}).");
            }

            using Stream stream = await response.Content.ReadAsStreamAsync(ct);
            using LimitedReadStream limited = new LimitedReadStream(stream, MaxDescriptorBytes);
            return await DescriptorParser.ReadAsync(limited, ct);
        }

        private static bool IsTransient(HttpRequestException ex)
        {
            // 5xx and "no status code" (DNS / TCP) are transient. 4xx is not — re-fetching
            // a 404 just wastes time.
            if (ex.StatusCode is HttpStatusCode code)
            {
                // 429 (Too Many Requests) and 408 (Request Timeout) are the server explicitly
                // asking us to retry — treat as transient, like 5xx. Other 4xx are not (BUG-041,
                // matches Hina.Core/Net/RetryPolicy.cs). Honoring Retry-After is out of scope here:
                // EnsureSuccessStatusCode discards the response headers.
                return code == HttpStatusCode.TooManyRequests   // 429
                    || code == HttpStatusCode.RequestTimeout    // 408
                    || (int)code >= 500;
            }
            return true;
        }

        private sealed class LimitedReadStream : Stream
        {
            private readonly Stream _inner;
            private readonly long _max;
            private long _read;

            public LimitedReadStream(Stream inner, long max) { _inner = inner; _max = max; }

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
                    throw new InvalidDataException($"Descriptor exceeded {_max} bytes.");
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
