using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Hina.PackageManager.Descriptor
{
    // Fetches a hina.app.json descriptor over HTTP. Capped at 5 MB to fail fast against
    // hostile/misconfigured servers serving giant payloads.
    public class DescriptorFetcher
    {
        public const long MaxDescriptorBytes = 5 * 1024 * 1024;
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

        private readonly HttpClient _http;

        public DescriptorFetcher(HttpClient? http = null)
        {
            _http = http ?? new HttpClient { Timeout = DefaultTimeout };
        }

        public virtual async Task<AppDescriptor> FetchAsync(Uri url, CancellationToken ct)
        {
            using HttpResponseMessage response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
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
