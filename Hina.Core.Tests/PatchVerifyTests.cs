using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
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
    // Whole-file verification (PatcherConfig.Verify): the rebuilt file must hash to the
    // manifest's FileHash regardless of how each chunk arrived (reused from local disk or
    // downloaded), and a manifest whose FileHash disagrees with its own chunks must fail
    // the patch even though every chunk passes its individual content-hash check.
    public class PatchVerifyTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string _sourceDir;
        private readonly string _buildOutputDir;
        private readonly string _targetDir;
        private readonly Uri _baseUrl = new Uri("http://test.local/");
        private const int ChunkSize = 4096;

        public PatchVerifyTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "hina_verify_" + Guid.NewGuid().ToString("N"));
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
        public async Task PatchAsync_VerifyEnabled_MixedMatchedAndDownloadedChunks_Succeeds()
        {
            // Target starts with the first half of the file intact, so the rebuild mixes
            // rsync-matched chunks (copied from local disk) with downloaded ones — the verify
            // hash must cover both paths.
            string content = GenerateData(ChunkSize * 8);
            CreateSourceFile("app.bin", content);
            await RunBuildAsync();

            string stale = content.Substring(0, ChunkSize * 4) + GenerateData(ChunkSize * 4, seed: 999);
            await File.WriteAllTextAsync(Path.Combine(_targetDir, "app.bin"), stale);

            PatchClient client = CreatePatchClient(verify: true);
            PatchResult result = await client.PatchAsync(_targetDir, CancellationToken.None);

            Assert.True(result.Success, $"Patch failed: {result.Message}");
            Assert.Equal(content, await File.ReadAllTextAsync(Path.Combine(_targetDir, "app.bin")));
        }

        [Fact]
        public async Task PatchAsync_VerifyEnabled_ManifestFileHashWrong_Fails()
        {
            // Every chunk is individually valid, but the manifest lies about the whole-file
            // hash. Only the Verify pass can catch this.
            CreateSourceFile("app.bin", GenerateData(ChunkSize * 4));
            Manifest.Manifest manifest = await RunBuildAsync();

            manifest.Files[0].FileHash = "sha256:" + new string('0', 64);
            await ManifestSerializer.WriteAsync(manifest, Path.Combine(_buildOutputDir, "manifest.json"), CancellationToken.None);

            PatchClient client = CreatePatchClient(verify: true);
            PatchResult result = await client.PatchAsync(_targetDir, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("hash mismatch", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task PatchAsync_VerifyDisabled_ManifestFileHashWrong_StillSucceeds()
        {
            // With Verify off the whole-file check is skipped by design (chunks are still
            // individually hash-checked). Guards against the verify path running when disabled.
            string content = GenerateData(ChunkSize * 4);
            CreateSourceFile("app.bin", content);
            Manifest.Manifest manifest = await RunBuildAsync();

            manifest.Files[0].FileHash = "sha256:" + new string('0', 64);
            await ManifestSerializer.WriteAsync(manifest, Path.Combine(_buildOutputDir, "manifest.json"), CancellationToken.None);

            PatchClient client = CreatePatchClient(verify: false);
            PatchResult result = await client.PatchAsync(_targetDir, CancellationToken.None);

            Assert.True(result.Success, $"Patch failed: {result.Message}");
            Assert.Equal(content, await File.ReadAllTextAsync(Path.Combine(_targetDir, "app.bin")));
        }

        // ---------- Helpers ----------

        private void CreateSourceFile(string relativePath, string content)
        {
            string fullPath = Path.Combine(_sourceDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        private static string GenerateData(int length, int seed = 1234)
        {
            var sb = new StringBuilder(length);
            Random rng = new Random(seed);
            for (int i = 0; i < length; i++) sb.Append((char)('A' + rng.Next(0, 26)));
            return sb.ToString();
        }

        private async Task<Manifest.Manifest> RunBuildAsync()
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
            return manifest;
        }

        private PatchClient CreatePatchClient(bool verify)
        {
            PatcherConfig config = new PatcherConfig
            {
                BaseUrl = _baseUrl,
                Channel = "stable",
                ChunkSize = ChunkSize,
                Concurrency = 4,
                Verify = verify,
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

        // Serves manifest.json and chunks straight from the build output directory.
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
