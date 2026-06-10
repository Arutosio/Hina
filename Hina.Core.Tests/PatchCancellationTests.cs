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
    // Ctrl+C while a patch is downloading chunks must surface as OperationCanceledException
    // (so callers report "cancelled", and `update --all` aborts) — not as a generic
    // PatchResult.Success = false. The on-disk state must still be rolled back.
    public class PatchCancellationTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string _sourceDir;
        private readonly string _buildOutputDir;
        private readonly string _targetDir;
        private readonly Uri _baseUrl = new Uri("http://test.local/");
        private const int ChunkSize = 4096;

        public PatchCancellationTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "hina_cancel_" + Guid.NewGuid().ToString("N"));
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
        public async Task PatchAsync_CancelledMidChunkDownload_PropagatesAndLeavesTargetIntact()
        {
            string content = GenerateData(ChunkSize * 8);
            CreateSourceFile("app.bin", content);
            await RunBuildAsync();

            // Stale target so the patch must download chunks.
            string stale = GenerateData(ChunkSize * 8, seed: 999);
            await File.WriteAllTextAsync(Path.Combine(_targetDir, "app.bin"), stale);

            using var cts = new CancellationTokenSource();
            PatchClient client = CreatePatchClient(cts);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.PatchAsync(_targetDir, cts.Token));

            // Original file untouched (the temp rebuild was never swapped in)...
            Assert.Equal(stale, await File.ReadAllTextAsync(Path.Combine(_targetDir, "app.bin")));
            // ...and no temp/journal litter blocks a retry.
            Assert.Empty(Directory.GetFiles(_targetDir, "*.hina.tmp", SearchOption.AllDirectories));
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

        private PatchClient CreatePatchClient(CancellationTokenSource cts)
        {
            PatcherConfig config = new PatcherConfig
            {
                BaseUrl = _baseUrl,
                Channel = "stable",
                ChunkSize = ChunkSize,
                Concurrency = 4,
                Verify = false,
                Backup = true,
                TrustedPublicKey = null
            };

            PatchClient client = new PatchClient(config);

            var httpClient = new HttpClient(new CancelOnFirstChunkHandler(_buildOutputDir, cts));
            var httpChunkClient = new HttpChunkClient(httpClient);
            FieldInfo? httpField = typeof(PatchClient).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(httpField);
            httpField!.SetValue(client, httpChunkClient);

            return client;
        }

        // Serves the manifest normally, then simulates the user pressing Ctrl+C as soon as
        // the first chunk request arrives.
        private sealed class CancelOnFirstChunkHandler : HttpMessageHandler
        {
            private readonly string _buildOutputDir;
            private readonly CancellationTokenSource _cts;

            public CancelOnFirstChunkHandler(string buildOutputDir, CancellationTokenSource cts)
            {
                _buildOutputDir = buildOutputDir;
                _cts = cts;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string path = request.RequestUri!.AbsolutePath.TrimStart('/');
                if (path.StartsWith("chunks/", StringComparison.Ordinal))
                {
                    _cts.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                }
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
