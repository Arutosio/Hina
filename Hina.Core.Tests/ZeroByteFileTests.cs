using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Chunking;
using Hina.Core.Configuration;
using Hina.Core.Hashing;
using Hina.Core.Manifest;
using Hina.Core.Net;
using Hina.Core.Patching;
using Xunit;

namespace Hina.Core.Tests
{
    // Zero-byte files are legitimate app content (placeholder configs, .gitkeep-style markers,
    // lock files). The build → manifest → patch pipeline must round-trip them.
    public class ZeroByteFileTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string _sourceDir;
        private readonly string _buildOutputDir;
        private readonly string _targetDir;
        private readonly Uri _baseUrl = new Uri("http://test.local/");
        private const int ChunkSize = 4096;

        public ZeroByteFileTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "hina_zero_" + Guid.NewGuid().ToString("N"));
            _sourceDir = Path.Combine(_tempRoot, "source");
            _buildOutputDir = Path.Combine(_tempRoot, "build");
            _targetDir = Path.Combine(_tempRoot, "target");
            Directory.CreateDirectory(_sourceDir);
            Directory.CreateDirectory(_buildOutputDir);
            Directory.CreateDirectory(_targetDir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); }
            catch { /* best effort */ }
        }

        [Fact]
        public async Task BuildAndPatch_ZeroByteFile_RoundTrips()
        {
            File.WriteAllText(Path.Combine(_sourceDir, "empty.cfg"), "");
            File.WriteAllText(Path.Combine(_sourceDir, "app.bin"), new string('A', ChunkSize * 2));

            await RunBuildAsync();

            PatchClient client = CreatePatchClient();
            PatchResult result = await client.PatchAsync(_targetDir, CancellationToken.None);

            Assert.True(result.Success, $"Patch failed: {result.Message}");
            string emptyPath = Path.Combine(_targetDir, "empty.cfg");
            Assert.True(File.Exists(emptyPath), "zero-byte file missing after patch");
            Assert.Equal(0, new FileInfo(emptyPath).Length);
            Assert.Equal(new string('A', ChunkSize * 2), await File.ReadAllTextAsync(Path.Combine(_targetDir, "app.bin")));
        }

        [Fact]
        public async Task BuildAndPatch_FileTruncatedToZero_UpdatesExistingTarget()
        {
            // v2 truncates a previously non-empty file: the patch must shrink it to 0 bytes.
            File.WriteAllText(Path.Combine(_sourceDir, "data.txt"), "");
            await RunBuildAsync();

            await File.WriteAllTextAsync(Path.Combine(_targetDir, "data.txt"), "stale non-empty content");

            PatchClient client = CreatePatchClient();
            PatchResult result = await client.PatchAsync(_targetDir, CancellationToken.None);

            Assert.True(result.Success, $"Patch failed: {result.Message}");
            Assert.Equal(0, new FileInfo(Path.Combine(_targetDir, "data.txt")).Length);
        }

        // ---------- Helpers ----------

        private async Task RunBuildAsync()
        {
            IHasher hasher = new Sha256Hasher();
            ManifestBuilder builder = new ManifestBuilder(hasher);
            ChunkStoreWriter chunkWriter = new ChunkStoreWriter(hasher, ChunkSize);

            DirectoryInfo sourceDir = new DirectoryInfo(_sourceDir);
            DirectoryInfo chunkDir = new DirectoryInfo(Path.Combine(_buildOutputDir, "chunks"));

            Manifest.Manifest manifest = await builder.BuildAsync(sourceDir, _baseUrl, ChunkSize, CancellationToken.None);
            manifest.Version = "1.0.0";

            await ManifestSerializer.WriteAsync(manifest, Path.Combine(_buildOutputDir, "manifest.json"), CancellationToken.None);
            await chunkWriter.WriteChunksAsync(sourceDir, chunkDir, CancellationToken.None);
        }

        private PatchClient CreatePatchClient()
        {
            PatcherConfig config = new PatcherConfig
            {
                BaseUrl = _baseUrl,
                Channel = "stable",
                ChunkSize = ChunkSize,
                Concurrency = 4,
                Verify = true,
                Backup = false,
                TrustedPublicKey = null
            };

            PatchClient client = new PatchClient(config);

            var httpClient = new HttpClient(new ServeDirHandler(_buildOutputDir));
            var httpChunkClient = new HttpChunkClient(httpClient);
            FieldInfo? httpField = typeof(PatchClient).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(httpField);
            httpField!.SetValue(client, httpChunkClient);

            return client;
        }

        private sealed class ServeDirHandler : HttpMessageHandler
        {
            private readonly string _buildOutputDir;

            public ServeDirHandler(string buildOutputDir)
            {
                _buildOutputDir = buildOutputDir;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string path = request.RequestUri!.AbsolutePath.TrimStart('/');
                string localPath = Path.Combine(_buildOutputDir, path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(localPath))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }
                byte[] data = await File.ReadAllBytesAsync(localPath, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
            }
        }
    }
}
