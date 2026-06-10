using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Chunking;
using Hina.Core.Configuration;
using Hina.Core.Hashing;
using Hina.Core.Inputs;
using Hina.Core.Manifest;
using Hina.Core.Net;
using Hina.Core.Patching;
using Xunit;

namespace Hina.Core.Tests
{
    // End-to-end proof for the --common merge: a manifest built from [common, variant]
    // installs both trees into one client root, and a later change to a COMMON file
    // delta-updates exactly that file (the variant's binary is not re-downloaded).
    public class CommonMergeRoundTripTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string _commonDir;
        private readonly string _variantDir;
        private readonly string _buildOutputDir;
        private readonly string _targetDir;
        private readonly Uri _baseUrl = new Uri("http://test.local/");
        private const int ChunkSize = 4096;

        public CommonMergeRoundTripTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "hina_common_rt_" + Guid.NewGuid().ToString("N"));
            _commonDir = Path.Combine(_tempRoot, "common");
            _variantDir = Path.Combine(_tempRoot, "variant");
            _buildOutputDir = Path.Combine(_tempRoot, "build");
            _targetDir = Path.Combine(_tempRoot, "target");
            Directory.CreateDirectory(_commonDir);
            Directory.CreateDirectory(_variantDir);
            Directory.CreateDirectory(_buildOutputDir);
            Directory.CreateDirectory(_targetDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best effort */ }
        }

        [Fact]
        public async Task BuildWithMergedCommon_ChangedCommonFile_DeltaUpdatesClient()
        {
            WriteFile(_commonDir, "data/map0.mul", "shared map data v1");
            WriteFile(_variantDir, "game.bin", "platform binary");

            await RunMergedBuildAsync("1.0.0");
            PatchResult v1 = await CreatePatchClient().PatchAsync(_targetDir, CancellationToken.None);
            Assert.True(v1.Success, $"v1 patch failed: {v1.Message}");
            Assert.Equal("shared map data v1", File.ReadAllText(Path.Combine(_targetDir, "data", "map0.mul")));
            Assert.Equal("platform binary", File.ReadAllText(Path.Combine(_targetDir, "game.bin")));

            // Clean the journal so the v2 patch does not roll back v1.
            string journalPath = PatchJournal.GetJournalPath(_targetDir);
            if (File.Exists(journalPath)) File.Delete(journalPath);

            // Only the COMMON file changes (the game data updated; the binary did not).
            WriteFile(_commonDir, "data/map0.mul", "shared map data v2 - patched");
            await RunMergedBuildAsync("1.0.1");

            PatchResult v2 = await CreatePatchClient().PatchAsync(_targetDir, CancellationToken.None);

            Assert.True(v2.Success, $"v2 patch failed: {v2.Message}");
            Assert.Single(v2.AppliedFiles);
            Assert.Contains("data/map0.mul", v2.AppliedFiles);
            Assert.Equal("shared map data v2 - patched", File.ReadAllText(Path.Combine(_targetDir, "data", "map0.mul")));
            Assert.Equal("platform binary", File.ReadAllText(Path.Combine(_targetDir, "game.bin")));
        }

        private static void WriteFile(string root, string relPath, string content)
        {
            string full = Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        private async Task RunMergedBuildAsync(string version)
        {
            IHasher hasher = new Sha256Hasher();
            ManifestBuilder builder = new ManifestBuilder(hasher);
            ChunkStoreWriter chunkWriter = new ChunkStoreWriter(hasher, ChunkSize);
            DirectoryInfo chunkDir = new DirectoryInfo(Path.Combine(_buildOutputDir, "chunks"));

            // Same precedence order as BuildCommand: common first, variant last (wins).
            InputSet inputs = InputSet.Resolve(new[]
            {
                new DirectoryInfo(_commonDir),
                new DirectoryInfo(_variantDir)
            });

            Manifest.Manifest manifest = await builder.BuildAsync(
                inputs, _baseUrl, ChunkSize, new Hina.Core.Rsync.RsyncChunker(ChunkSize, hasher), CancellationToken.None);
            manifest.Version = version;

            await ManifestSerializer.WriteAsync(manifest, Path.Combine(_buildOutputDir, "manifest.json"), CancellationToken.None);
            await chunkWriter.WriteChunksAsync(inputs, chunkDir, CancellationToken.None);
        }

        private PatchClient CreatePatchClient()
        {
            PatcherConfig config = new PatcherConfig
            {
                BaseUrl = _baseUrl,
                Channel = "stable",
                ChunkSize = ChunkSize,
                Verify = true,
                Backup = true,
                TrustedPublicKey = null
            };

            PatchClient client = new PatchClient(config);

            var httpChunkClient = new HttpChunkClient(new HttpClient(new LocalChunkHandler(_buildOutputDir)));
            FieldInfo? httpField = typeof(PatchClient).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(httpField);
            httpField!.SetValue(client, httpChunkClient);

            return client;
        }
    }
}
