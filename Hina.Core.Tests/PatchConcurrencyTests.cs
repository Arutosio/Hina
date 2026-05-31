using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Chunking;
using Hina.Core.Compression;
using Hina.Core.Configuration;
using Hina.Core.Hashing;
using Hina.Core.Manifest;
using Hina.Core.Net;
using Hina.Core.Patching;
using Xunit;

namespace Hina.Core.Tests
{
    // A1: PatchClient should download missing chunks in parallel, bounded by Config.Concurrency,
    // while still writing the output file byte-identical in manifest order.
    public class PatchConcurrencyTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string _sourceDir;
        private readonly string _buildOutputDir;
        private readonly string _targetDir;
        private readonly Uri _baseUrl = new Uri("http://test.local/");
        private const int ChunkSize = 4096;

        public PatchConcurrencyTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "hina_concurrency_" + Guid.NewGuid().ToString("N"));
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
        public async Task PatchAsync_ManyMissingChunks_DownloadsConcurrentlyBoundedByConcurrency()
        {
            // A file spanning many chunks; empty target → every chunk is downloaded.
            CreateSourceFile("big.bin", GenerateData(ChunkSize * 12));
            await RunBuildAsync();

            const int concurrency = 4;
            var handler = new ConcurrencyCountingHandler(_buildOutputDir, perRequestDelayMs: 40);
            PatchClient client = CreatePatchClient(concurrency, handler);

            PatchResult result = await client.PatchAsync(_targetDir, CancellationToken.None);

            Assert.True(result.Success, $"Patch failed: {result.Message}");
            AssertFileContentsMatch("big.bin");

            // Parallelism actually happened...
            Assert.True(handler.MaxConcurrent >= 2,
                $"Expected concurrent chunk downloads, saw max {handler.MaxConcurrent}.");
            // ...but never exceeded the configured cap.
            Assert.True(handler.MaxConcurrent <= concurrency,
                $"Concurrency cap {concurrency} exceeded: saw {handler.MaxConcurrent}.");
        }

        [Fact]
        public async Task PatchAsync_Concurrency1_SerializesDownloads()
        {
            CreateSourceFile("big.bin", GenerateData(ChunkSize * 8));
            await RunBuildAsync();

            var handler = new ConcurrencyCountingHandler(_buildOutputDir, perRequestDelayMs: 20);
            PatchClient client = CreatePatchClient(concurrency: 1, handler);

            PatchResult result = await client.PatchAsync(_targetDir, CancellationToken.None);

            Assert.True(result.Success, $"Patch failed: {result.Message}");
            AssertFileContentsMatch("big.bin");
            Assert.Equal(1, handler.MaxConcurrent);
        }

        // ---------- Helpers ----------

        private void CreateSourceFile(string relativePath, string content)
        {
            string fullPath = Path.Combine(_sourceDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        private static string GenerateData(int length)
        {
            var sb = new StringBuilder(length);
            Random rng = new Random(1234);
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

        [Fact]
        public async Task PatchAsync_DownloadFails_CancelsAndDrainsInFlightDownloads()
        {
            // Many missing chunks; the first-started download fails fast (corrupt) while the
            // others are still in flight. The patch must cancel + drain them before returning.
            CreateSourceFile("big.bin", GenerateData(ChunkSize * 8));
            await RunBuildAsync();

            var handler = new FailFirstDrainHandler(_buildOutputDir, siblingDelayMs: 3000);
            PatchClient client = CreatePatchClient(concurrency: 4, handler);

            PatchResult result = await client.PatchAsync(_targetDir, CancellationToken.None);

            Assert.False(result.Success);
            // No download left running: without cancel+drain the siblings would still be in
            // their 3s delay when PatchAsync returns.
            Assert.Equal(0, handler.InFlight);
        }

        private PatchClient CreatePatchClient(int concurrency, HttpMessageHandler handler)
        {
            PatcherConfig config = new PatcherConfig
            {
                BaseUrl = _baseUrl,
                Channel = "stable",
                ChunkSize = ChunkSize,
                Concurrency = concurrency,
                Verify = true,
                Backup = false,
                TrustedPublicKey = null
            };

            PatchClient client = new PatchClient(config);

            var httpClient = new HttpClient(handler);
            var httpChunkClient = new HttpChunkClient(httpClient);
            FieldInfo? httpField = typeof(PatchClient).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(httpField);
            httpField!.SetValue(client, httpChunkClient);

            return client;
        }

        private void AssertFileContentsMatch(string relativePath)
        {
            string sourcePath = Path.Combine(_sourceDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            string targetPath = Path.Combine(_targetDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(targetPath), $"Target file missing: {relativePath}");
            Assert.Equal(File.ReadAllBytes(sourcePath), File.ReadAllBytes(targetPath));
        }

        // Serves the manifest, fails the first-started chunk download immediately (corrupt), and
        // makes every subsequent chunk download block on a long delay so the test can observe
        // whether the patch cancels + drains them on failure. Tracks currently in-flight downloads.
        private sealed class FailFirstDrainHandler : HttpMessageHandler
        {
            private readonly string _buildOutputDir;
            private readonly int _siblingDelayMs;
            private int _started;
            private int _inFlight;

            public FailFirstDrainHandler(string buildOutputDir, int siblingDelayMs)
            {
                _buildOutputDir = buildOutputDir;
                _siblingDelayMs = siblingDelayMs;
            }

            public int InFlight => Volatile.Read(ref _inFlight);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string path = request.RequestUri!.AbsolutePath.TrimStart('/');

                if (path.StartsWith("manifest", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] data = await File.ReadAllBytesAsync(Path.Combine(_buildOutputDir, "manifest.json"), cancellationToken);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
                }

                if (path.StartsWith("chunks/", StringComparison.OrdinalIgnoreCase))
                {
                    // The increment runs synchronously before any await, so n==1 is the first
                    // chunk download started by the rebuild loop (write position 0).
                    int n = Interlocked.Increment(ref _started);
                    if (n == 1)
                    {
                        // Corrupt content → per-chunk hash verify throws → patch fails fast.
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new ByteArrayContent(BrotliCodec.Compress(new byte[] { 9, 9, 9, 9 }))
                        };
                    }

                    Interlocked.Increment(ref _inFlight);
                    try { await Task.Delay(_siblingDelayMs, cancellationToken); }
                    finally { Interlocked.Decrement(ref _inFlight); }
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(BrotliCodec.Compress(new byte[] { 9 }))
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        // Serves manifest + chunks from the build dir, delays each chunk response so overlapping
        // requests are observable, and tracks the peak number of in-flight chunk requests.
        private sealed class ConcurrencyCountingHandler : HttpMessageHandler
        {
            private readonly string _buildOutputDir;
            private readonly int _perRequestDelayMs;
            private int _current;
            private int _max;
            private readonly object _gate = new object();

            public ConcurrencyCountingHandler(string buildOutputDir, int perRequestDelayMs)
            {
                _buildOutputDir = buildOutputDir;
                _perRequestDelayMs = perRequestDelayMs;
            }

            public int MaxConcurrent { get { lock (_gate) { return _max; } } }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string path = request.RequestUri!.AbsolutePath.TrimStart('/');

                if (path.StartsWith("manifest", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    string manifestPath = Path.Combine(_buildOutputDir, "manifest.json");
                    byte[] data = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
                }

                if (path.StartsWith("chunks/", StringComparison.OrdinalIgnoreCase))
                {
                    int now = Interlocked.Increment(ref _current);
                    lock (_gate) { if (now > _max) _max = now; }
                    try
                    {
                        await Task.Delay(_perRequestDelayMs, cancellationToken);
                        string chunkPath = Path.Combine(_buildOutputDir, path.Replace('/', Path.DirectorySeparatorChar));
                        if (!File.Exists(chunkPath))
                        {
                            return new HttpResponseMessage(HttpStatusCode.NotFound);
                        }
                        byte[] data = await File.ReadAllBytesAsync(chunkPath, cancellationToken);
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _current);
                    }
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }
    }
}
