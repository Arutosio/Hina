using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Compression;
using Hina.Core.Net;
using Xunit;

namespace Hina.Core.Tests
{
    public class RetryTests
    {
        private static readonly Uri BaseUrl = new Uri("http://cdn.test.com/");

        // Chunks are content-addressed and HttpChunkClient now verifies the decompressed bytes
        // hash to the requested name, so success-path tests must request the real content hash.
        private static string Hash(byte[] content) =>
            "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));

        [Fact]
        public async Task GetChunkAsync_SuccessOnFirstAttempt_NoRetry()
        {
            int callCount = 0;
            byte[] original = new byte[] { 1, 2, 3 };
            byte[] compressed = BrotliCodec.Compress(original);

            var handler = new SequenceHandler(new[]
            {
                (Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>)((req, ct) =>
                {
                    callCount++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(compressed)
                    });
                })
            });

            using var http = new HttpClient(handler);
            var policy = new RetryPolicy(maxRetries: 3, baseDelayMs: 10, jitterRng: new Random(42));
            var client = new HttpChunkClient(http, policy);

            byte[] result = await client.GetChunkAsync(BaseUrl, Hash(original), CancellationToken.None);

            Assert.Equal(original, result);
            Assert.Equal(1, callCount);
        }

        [Fact]
        public async Task GetChunkAsync_RetryOn500ThenSuccess()
        {
            int callCount = 0;
            byte[] original = new byte[] { 10, 20, 30 };
            byte[] compressed = BrotliCodec.Compress(original);

            var handler = new SequenceHandler(new Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[]
            {
                (req, ct) =>
                {
                    callCount++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("Server Error")
                    });
                },
                (req, ct) =>
                {
                    callCount++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(compressed)
                    });
                }
            });

            using var http = new HttpClient(handler);
            var policy = new RetryPolicy(maxRetries: 3, baseDelayMs: 10, jitterRng: new Random(42));
            var client = new HttpChunkClient(http, policy);

            byte[] result = await client.GetChunkAsync(BaseUrl, Hash(original), CancellationToken.None);

            Assert.Equal(original, result);
            Assert.Equal(2, callCount);
        }

        [Fact]
        public async Task GetChunkAsync_RetryOnHttpRequestExceptionThenSuccess()
        {
            int callCount = 0;
            byte[] original = new byte[] { 5, 6, 7 };
            byte[] compressed = BrotliCodec.Compress(original);

            var handler = new SequenceHandler(new Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[]
            {
                (req, ct) =>
                {
                    callCount++;
                    throw new HttpRequestException("Connection refused");
                },
                (req, ct) =>
                {
                    callCount++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(compressed)
                    });
                }
            });

            using var http = new HttpClient(handler);
            var policy = new RetryPolicy(maxRetries: 3, baseDelayMs: 10, jitterRng: new Random(42));
            var client = new HttpChunkClient(http, policy);

            byte[] result = await client.GetChunkAsync(BaseUrl, Hash(original), CancellationToken.None);

            Assert.Equal(original, result);
            Assert.Equal(2, callCount);
        }

        [Fact]
        public async Task GetChunkAsync_RetryOnCorruptChunkThenSuccess()
        {
            // A CDN edge serving one corrupt object (hash mismatch) is transient: a retry can hit
            // a healthy node. The chunk is content-addressed, so integrity failures are safe to retry.
            int callCount = 0;
            byte[] original = new byte[] { 1, 2, 3, 4 };
            byte[] good = BrotliCodec.Compress(original);
            byte[] corrupt = BrotliCodec.Compress(new byte[] { 9, 9, 9, 9 });

            var handler = new SequenceHandler(new Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[]
            {
                (req, ct) =>
                {
                    callCount++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(corrupt)
                    });
                },
                (req, ct) =>
                {
                    callCount++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(good)
                    });
                }
            });

            using var http = new HttpClient(handler);
            var policy = new RetryPolicy(maxRetries: 3, baseDelayMs: 10, jitterRng: new Random(42));
            var client = new HttpChunkClient(http, policy);

            byte[] result = await client.GetChunkAsync(BaseUrl, Hash(original), CancellationToken.None);

            Assert.Equal(original, result);
            Assert.Equal(2, callCount);
        }

        [Fact]
        public async Task GetChunkAsync_RetryOnUndecodableChunkThenSuccess()
        {
            // Corrupt-at-rest objects usually fail Brotli decode before the hash check runs;
            // that's the same transient store corruption and gets the same retry.
            int callCount = 0;
            byte[] original = new byte[] { 5, 6, 7, 8 };
            byte[] good = BrotliCodec.Compress(original);
            byte[] notBrotli = new byte[] { 0xFF, 0xFE, 0xFD, 0xFC, 0xFB };

            var handler = new SequenceHandler(new Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[]
            {
                (req, ct) =>
                {
                    callCount++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(notBrotli)
                    });
                },
                (req, ct) =>
                {
                    callCount++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(good)
                    });
                }
            });

            using var http = new HttpClient(handler);
            var policy = new RetryPolicy(maxRetries: 3, baseDelayMs: 10, jitterRng: new Random(42));
            var client = new HttpChunkClient(http, policy);

            byte[] result = await client.GetChunkAsync(BaseUrl, Hash(original), CancellationToken.None);

            Assert.Equal(original, result);
            Assert.Equal(2, callCount);
        }

        [Fact]
        public async Task GetChunkAsync_CorruptChunkExhaustsRetries_Throws()
        {
            // Persistently corrupt chunk (tampered store): retries are bounded, then the
            // integrity error propagates so the install fails loudly instead of looping.
            int callCount = 0;
            byte[] corrupt = BrotliCodec.Compress(new byte[] { 9, 9, 9, 9 });

            var handler = new SequenceHandler(new Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[]
            {
                (req, ct) =>
                {
                    callCount++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(corrupt)
                    });
                }
            });

            using var http = new HttpClient(handler);
            var policy = new RetryPolicy(maxRetries: 2, baseDelayMs: 10, jitterRng: new Random(42));
            var client = new HttpChunkClient(http, policy);

            await Assert.ThrowsAsync<ChunkIntegrityException>(() => client.GetChunkAsync(
                BaseUrl, Hash(new byte[] { 1, 2, 3, 4 }), CancellationToken.None));
            Assert.Equal(3, callCount); // initial attempt + 2 retries
        }

        [Fact]
        public async Task GetChunkAsync_NoRetryOn404()
        {
            int callCount = 0;

            var handler = new SequenceHandler(new Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[]
            {
                (req, ct) =>
                {
                    callCount++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent("Not Found")
                    });
                },
                // This should never be reached
                (req, ct) =>
                {
                    callCount++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(Array.Empty<byte>())
                    });
                }
            });

            using var http = new HttpClient(handler);
            var policy = new RetryPolicy(maxRetries: 3, baseDelayMs: 10, jitterRng: new Random(42));
            var client = new HttpChunkClient(http, policy);

            await Assert.ThrowsAsync<HttpRequestException>(() =>
                client.GetChunkAsync(BaseUrl, "sha256:aabb001122", CancellationToken.None));

            Assert.Equal(1, callCount);
        }

        [Fact]
        public async Task GetChunkAsync_MaxRetriesExhausted_Throws()
        {
            int callCount = 0;

            var handler = new SequenceHandler(new Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[]
            {
                (req, ct) => { callCount++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)); },
                (req, ct) => { callCount++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)); },
                (req, ct) => { callCount++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)); },
                (req, ct) => { callCount++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)); },
            });

            using var http = new HttpClient(handler);
            var policy = new RetryPolicy(maxRetries: 3, baseDelayMs: 10, jitterRng: new Random(42));
            var client = new HttpChunkClient(http, policy);

            var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
                client.GetChunkAsync(BaseUrl, "sha256:aabb001122", CancellationToken.None));

            Assert.Contains("failed after", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(4, callCount); // 1 initial + 3 retries
        }

        [Fact]
        public async Task GetChunkAsync_CancellationDuringRetry_Throws()
        {
            int callCount = 0;
            var cts = new CancellationTokenSource();

            var handler = new SequenceHandler(new Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[]
            {
                (req, ct) =>
                {
                    callCount++;
                    // Cancel after the first failure, before the retry delay completes
                    cts.Cancel();
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                },
                (req, ct) =>
                {
                    callCount++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(Array.Empty<byte>())
                    });
                }
            });

            using var http = new HttpClient(handler);
            var policy = new RetryPolicy(maxRetries: 3, baseDelayMs: 1000, jitterRng: new Random(42));
            var client = new HttpChunkClient(http, policy);

            // Cancellation is honoured before the 500 is treated as a retryable error, and no retry
            // is attempted (callCount stays 1). Type is OperationCanceledException (TaskCanceledException
            // derives from it), so accept any.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                client.GetChunkAsync(BaseUrl, "sha256:aabb001122", cts.Token));

            Assert.Equal(1, callCount);
        }

        [Fact]
        public void IsTransient_ServerErrors_ReturnsTrue()
        {
            Assert.True(RetryPolicy.IsTransient(new HttpRequestException("err", null, HttpStatusCode.InternalServerError)));
            Assert.True(RetryPolicy.IsTransient(new HttpRequestException("err", null, HttpStatusCode.BadGateway)));
            Assert.True(RetryPolicy.IsTransient(new HttpRequestException("err", null, HttpStatusCode.ServiceUnavailable)));
            Assert.True(RetryPolicy.IsTransient(new HttpRequestException("err", null, HttpStatusCode.GatewayTimeout)));
        }

        [Fact]
        public void IsTransient_ClientErrors_ReturnsFalse()
        {
            Assert.False(RetryPolicy.IsTransient(new HttpRequestException("err", null, HttpStatusCode.NotFound)));
            Assert.False(RetryPolicy.IsTransient(new HttpRequestException("err", null, HttpStatusCode.BadRequest)));
            Assert.False(RetryPolicy.IsTransient(new HttpRequestException("err", null, HttpStatusCode.Forbidden)));
            Assert.False(RetryPolicy.IsTransient(new HttpRequestException("err", null, HttpStatusCode.Unauthorized)));
        }

        [Fact]
        public void IsTransient_NetworkError_NoStatusCode_ReturnsTrue()
        {
            Assert.True(RetryPolicy.IsTransient(new HttpRequestException("Connection refused")));
        }

        [Fact]
        public void CalculateDelay_ExponentialBackoff()
        {
            // Use deterministic RNG with seed 0 for predictable jitter
            var policy = new RetryPolicy(maxRetries: 3, baseDelayMs: 100, jitterRng: new Random(0));

            int delay1 = policy.CalculateDelay(1); // 100 * 2^0 + jitter
            int delay2 = policy.CalculateDelay(2); // 100 * 2^1 + jitter
            int delay3 = policy.CalculateDelay(3); // 100 * 2^2 + jitter

            // Verify exponential growth (base without jitter: 100, 200, 400)
            Assert.InRange(delay1, 100, 125); // base 100 + up to 25 jitter
            Assert.InRange(delay2, 200, 250); // base 200 + up to 50 jitter
            Assert.InRange(delay3, 400, 500); // base 400 + up to 100 jitter
        }

        /// <summary>
        /// Handler that returns different responses for sequential requests.
        /// </summary>
        private sealed class SequenceHandler : HttpMessageHandler
        {
            private readonly IReadOnlyList<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses;
            private int _index;

            public SequenceHandler(IReadOnlyList<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> responses)
            {
                _responses = responses;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                int idx = _index;
                if (idx < _responses.Count)
                {
                    _index++;
                }
                else
                {
                    idx = _responses.Count - 1;
                }

                return _responses[idx](request, cancellationToken);
            }
        }
    }
}
