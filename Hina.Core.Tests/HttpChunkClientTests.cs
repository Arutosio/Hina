using System;
using System.Collections.Generic;
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

        [Fact]
        public async Task GetChunkAsync_ConstructsCorrectUrl()
        {
            byte[] originalData = Encoding.UTF8.GetBytes("chunk data here");
            byte[] compressed = BrotliCodec.Compress(originalData);

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
                "sha256:aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899",
                CancellationToken.None);

            // Bucket is first 2 chars of hash-only part
            Assert.Equal("http://cdn.test.com/chunks/aa/aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899.chunk.br",
                capturedUri!.ToString());
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
                "sha256:ff11223344",
                CancellationToken.None);

            Assert.Equal(original, result);
        }

        [Fact]
        public async Task GetChunkAsync_HashWithoutPrefix_StillWorks()
        {
            byte[] compressed = BrotliCodec.Compress(new byte[] { 0xAB });

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

            await client.GetChunkAsync(new Uri("http://cdn.test.com/"), "deadbeef0011", CancellationToken.None);

            // No colon, so the entire string is used as hash
            Assert.Contains("chunks/de/deadbeef0011.chunk.br", capturedUri!.ToString());
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
