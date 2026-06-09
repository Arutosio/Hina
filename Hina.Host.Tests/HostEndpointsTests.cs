using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Hina.Host.Tests
{
    // Boots the real Hina.Host pipeline in-process (TestServer). The patch root is pointed
    // at a temp directory via the legacy Patcher:Root setting, which HostOptions.Load reads
    // from IConfiguration.
    public class HostEndpointsTests : IDisposable
    {
        private readonly string _root;
        private readonly WebApplicationFactory<Program> _factory;

        public HostEndpointsTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "hina_host_it_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "chunks", "ab"));
            File.WriteAllText(Path.Combine(_root, "manifest.json"), """{ "version": "1.0.0" }""");
            File.WriteAllBytes(Path.Combine(_root, "chunks", "ab", "abcd.chunk.br"), new byte[] { 1, 2, 3 });

            _factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(b => b.UseSetting("Patcher:Root", _root));
        }

        public void Dispose()
        {
            _factory.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }

        [Fact]
        public async Task Health_ReturnsOk()
        {
            using var client = _factory.CreateClient();
            var resp = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        [Fact]
        public async Task Stats_NonLoopbackCaller_Returns404()
        {
            // TestServer connections have no RemoteIpAddress, which must NOT pass the
            // loopback-only gate.
            using var client = _factory.CreateClient();
            var resp = await client.GetAsync("/stats");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }

        [Fact]
        public async Task Manifest_IsServed_WithRevalidateCacheHeader()
        {
            using var client = _factory.CreateClient();
            var resp = await client.GetAsync("/manifest.json");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            string cache = resp.Headers.TryGetValues("Cache-Control", out var v) ? string.Join(",", v) : "";
            Assert.Contains("must-revalidate", cache);
        }

        [Fact]
        public async Task Chunk_IsServed_WithImmutableCacheHeader()
        {
            using var client = _factory.CreateClient();
            var resp = await client.GetAsync("/chunks/ab/abcd.chunk.br");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            string cache = resp.Headers.TryGetValues("Cache-Control", out var v) ? string.Join(",", v) : "";
            Assert.Contains("immutable", cache);
        }

        [Fact]
        public async Task MissingFile_Returns404()
        {
            using var client = _factory.CreateClient();
            var resp = await client.GetAsync("/nope/missing.bin");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
    }
}
