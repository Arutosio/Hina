using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Compression;
using Hina.Core.Manifest;
using Hina.Core.Net;
using Xunit;

namespace Hina.Core.Tests
{
    public class HttpChunkClientTests
    {
        [Fact]
        public async Task GetManifestAsync_StableChannel_UsesManifestJson()
        {
            var manifest = new Manifest.Manifest
            {
                Version = "1.0.0",
                BaseUrl = "http://cdn.test.com/"
            };
            string json = JsonSerializer.Serialize(manifest);

            Uri? capturedUri = null;
            var handler = new FakeHandler((req, ct) =>
            {
                capturedUri = req.RequestUri;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            });

            using var http = new HttpClient(handler);
            var client = new HttpChunkClient(http);

            var result = await client.GetManifestAsync(new Uri("http://cdn.test.com/"), "stable", CancellationToken.None);

            Assert.Equal("1.0.0", result.Version);
            Assert.Equal("http://cdn.test.com/manifest.json", capturedUri!.ToString());
        }

        [Fact]
        public async Task GetManifestAsync_NonStableChannel_UsesChannelInFilename()
        {
            string json = JsonSerializer.Serialize(new Manifest.Manifest { Version = "2.0.0-beta" });

            Uri? capturedUri = null;
            var handler = new FakeHandler((req, ct) =>
            {
                capturedUri = req.RequestUri;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            });

            using var http = new HttpClient(handler);
            var client = new HttpChunkClient(http);

            await client.GetManifestAsync(new Uri("http://cdn.test.com/"), "beta", CancellationToken.None);

            Assert.Equal("http://cdn.test.com/manifest.beta.json", capturedUri!.ToString());
        }

        private static string Sha256Hex(byte[] data) =>
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data));

        [Fact]
        public async Task GetChunkAsync_ConstructsCorrectUrl()
        {
            byte[] originalData = Encoding.UTF8.GetBytes("chunk data here");
            byte[] compressed = BrotliCodec.Compress(originalData);
            // Chunks are content-addressed: the URL must use the real SHA-256 of the content.
            string hash = Sha256Hex(originalData);

            Uri? capturedUri = null;
            var handler = new FakeHandler((req, ct) =>
            {
                capturedUri = req.RequestUri;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(compressed)
                });
            });

            using var http = new HttpClient(handler);
            var client = new HttpChunkClient(http);

            byte[] result = await client.GetChunkAsync(
                new Uri("http://cdn.test.com/"),
                "sha256:" + hash,
                CancellationToken.None);

            Assert.Equal($"http://cdn.test.com/chunks/{hash.Substring(0, 2)}/{hash}.chunk.br", capturedUri!.ToString());
            Assert.Equal(originalData, result);
        }

        [Fact]
        public async Task GetChunkAsync_DecompressesBrotliResponse()
        {
            byte[] original = new byte[] { 1, 2, 3, 4, 5 };
            byte[] compressed = BrotliCodec.Compress(original);

            var handler = new FakeHandler((req, ct) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(compressed)
                }));

            using var http = new HttpClient(handler);
            var client = new HttpChunkClient(http);

            byte[] result = await client.GetChunkAsync(
                new Uri("http://cdn.test.com/"),
                "sha256:" + Sha256Hex(original),
                CancellationToken.None);

            Assert.Equal(original, result);
        }

        [Fact]
        public async Task GetChunkAsync_HashWithoutPrefix_StillWorks()
        {
            byte[] content = new byte[] { 0xAB };
            byte[] compressed = BrotliCodec.Compress(content);
            string hash = Sha256Hex(content);

            Uri? capturedUri = null;
            var handler = new FakeHandler((req, ct) =>
            {
                capturedUri = req.RequestUri;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(compressed)
                });
            });

            using var http = new HttpClient(handler);
            var client = new HttpChunkClient(http);

            await client.GetChunkAsync(new Uri("http://cdn.test.com/"), hash, CancellationToken.None);

            // No colon, so the entire string is used as hash
            Assert.Contains($"chunks/{hash.Substring(0, 2)}/{hash}.chunk.br", capturedUri!.ToString());
        }

        [Fact]
        public async Task GetChunkAsync_ContentHashMismatch_Throws()
        {
            // Server returns content that does not hash to the requested chunk name.
            byte[] tampered = Encoding.UTF8.GetBytes("not what was asked for");
            byte[] compressed = BrotliCodec.Compress(tampered);

            var handler = new FakeHandler((req, ct) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(compressed)
                }));

            using var http = new HttpClient(handler);
            var client = new HttpChunkClient(http);

            await Assert.ThrowsAsync<InvalidDataException>(() => client.GetChunkAsync(
                new Uri("http://cdn.test.com/"),
                "sha256:" + Sha256Hex(Encoding.UTF8.GetBytes("the real expected content")),
                CancellationToken.None));
        }

        [Fact]
        public async Task GetManifestAsync_HttpsRedirectedToHttp_Throws()
        {
            string json = JsonSerializer.Serialize(new Manifest.Manifest { Version = "1.0.0" });
            var handler = new FakeHandler((req, ct) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://cdn.test/manifest.json")
                }));
            using var http = new HttpClient(handler);
            var client = new HttpChunkClient(http);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                client.GetManifestAsync(new Uri("https://cdn.test/"), "stable", CancellationToken.None));
        }

        [Fact]
        public async Task GetManifestAsync_OversizedContentLength_Throws()
        {
            var handler = new FakeHandler((req, ct) =>
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1 }) };
                resp.Content.Headers.ContentLength = HttpChunkClient.MaxManifestBytes + 1;
                return Task.FromResult(resp);
            });
            using var http = new HttpClient(handler);
            var client = new HttpChunkClient(http);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                client.GetManifestAsync(new Uri("https://cdn.test/"), "stable", CancellationToken.None));
        }

        [Fact]
        public async Task GetChunkAsync_HttpsRedirectedToHttp_Throws()
        {
            byte[] data = new byte[] { 1, 2, 3 };
            byte[] compressed = BrotliCodec.Compress(data);
            var handler = new FakeHandler((req, ct) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(compressed),
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://cdn.test/chunks/aa/x.chunk.br")
                }));
            using var http = new HttpClient(handler);
            var client = new HttpChunkClient(http);

            await Assert.ThrowsAsync<InvalidDataException>(() => client.GetChunkAsync(
                new Uri("https://cdn.test/"), "sha256:" + Sha256Hex(data), CancellationToken.None, data.Length));
        }

        // Minimal fake HttpMessageHandler for testing
        private sealed class FakeHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

            public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            {
                _handler = handler;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return _handler(request, cancellationToken);
            }
        }
    }
}
