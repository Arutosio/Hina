using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
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
    // BUG-032: the final swap onto the live file must be an atomic rename (File.Move), not an
    // in-place File.Copy(overwrite) that truncates-and-rewrites the production binary and can
    // leave it corrupt on a crash mid-copy.
    public class PatchAtomicSwapTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string _sourceDir;
        private readonly string _buildOutputDir;
        private readonly string _targetDir;
        private readonly Uri _baseUrl = new Uri("http://test.local/");
        private const int ChunkSize = 4096;

        public PatchAtomicSwapTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "hina_swap_" + Guid.NewGuid().ToString("N"));
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

        // The swap renames the rebuilt temp onto the live path. A reader holding the OLD file open
        // across the swap keeps seeing the OLD bytes (the original inode is untouched), proving the
        // file was replaced atomically rather than rewritten in place. With File.Copy(overwrite)
        // the original inode is truncated-and-rewritten, so the held reader would observe the new
        // (or truncated) content — i.e. the production binary was mutated in place. POSIX-only:
        // Windows replace-while-open semantics differ; the real cross-platform gate is CI Linux/macOS.
        [Fact]
        public async Task PatchAsync_SwapIsAtomicRename_NotInPlaceRewrite()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return; // see note above
            }

            string newContent = GenerateData(ChunkSize * 8);
            CreateSourceFile("app.bin", newContent);
            await RunBuildAsync();

            // Target starts stale (second half differs) so the patch genuinely rebuilds + swaps.
            string staleContent = newContent.Substring(0, ChunkSize * 4) + GenerateData(ChunkSize * 4, seed: 999);
            string targetFile = Path.Combine(_targetDir, "app.bin");
            await File.WriteAllTextAsync(targetFile, staleContent);

            byte[] staleBytes = Encoding.UTF8.GetBytes(staleContent);

            // Hold the ORIGINAL file open across the swap.
            using (FileStream held = new FileStream(targetFile, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                PatchClient client = CreatePatchClient(verify: true);
                PatchResult result = await client.PatchAsync(_targetDir, CancellationToken.None);
                Assert.True(result.Success, $"Patch failed: {result.Message}");

                // The held reader still sees the OLD content -> the live file was renamed away,
                // not rewritten in place. (File.Copy would have truncated this same inode.)
                held.Seek(0, SeekOrigin.Begin);
                byte[] viaHeld = new byte[staleBytes.Length];
                int read = 0;
                while (read < viaHeld.Length)
                {
                    int n = await held.ReadAsync(viaHeld.AsMemory(read), CancellationToken.None);
                    if (n == 0) break;
                    read += n;
                }
                Assert.Equal(staleBytes.Length, read);
                Assert.Equal(staleBytes, viaHeld);
            }

            // On disk the path now resolves to the new content, and the temp was consumed.
            Assert.Equal(newContent, await File.ReadAllTextAsync(targetFile));
            Assert.False(File.Exists(targetFile + ".hina.tmp"), "temp file must be consumed by the rename");
        }

        // ---------- Helpers (mirrors PatchVerifyTests) ----------

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

        private sealed class ServeDirHandler : HttpMessageHandler
        {
            private readonly string _buildOutputDir;

            public ServeDirHandler(string buildOutputDir) => _buildOutputDir = buildOutputDir;

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
