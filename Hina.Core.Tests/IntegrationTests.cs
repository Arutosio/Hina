using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
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
    public class IntegrationTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string _sourceDir;
        private readonly string _buildOutputDir;
        private readonly string _targetDir;
        private readonly Uri _baseUrl = new Uri("http://test.local/");
        private const int ChunkSize = 4096; // Small chunks for testing

        public IntegrationTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "hina_integration_" + Guid.NewGuid().ToString("N"));
            _sourceDir = Path.Combine(_tempRoot, "source");
            _buildOutputDir = Path.Combine(_tempRoot, "build");
            _targetDir = Path.Combine(_tempRoot, "target");

            Directory.CreateDirectory(_sourceDir);
            Directory.CreateDirectory(_buildOutputDir);
            Directory.CreateDirectory(_targetDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempRoot))
                {
                    Directory.Delete(_tempRoot, recursive: true);
                }
            }
            catch
            {
                // Best effort cleanup.
            }
        }

        [Fact]
        public async Task FullBuildThenPatch_EmptyTarget_AllFilesPatched()
        {
            // Arrange: create source files.
            CreateSourceFile("data/config.txt", "server=localhost\nport=8080\n");
            CreateSourceFile("bin/app.dat", GenerateData(12000)); // Spans multiple chunks.
            CreateSourceFile("readme.txt", "Hello World");

            // Build manifest and chunks.
            await RunBuildAsync();

            // Act: patch to an empty target directory.
            PatchClient client = CreatePatchClient(backup: true, verify: true);
            PatchResult result = await client.PatchAsync(_targetDir, CancellationToken.None);

            // Assert.
            Assert.True(result.Success, $"Patch failed: {result.Message}");
            Assert.Equal(3, result.AppliedFiles.Count);

            AssertFileContentsMatch("data/config.txt");
            AssertFileContentsMatch("bin/app.dat");
            AssertFileContentsMatch("readme.txt");
        }

        [Fact]
        public async Task IncrementalUpdate_TargetHasOlderVersion_OnlyChangedFilesPatched()
        {
            // Arrange: build v1 and patch to target.
            CreateSourceFile("data/config.txt", "server=localhost\nport=8080\n");
            CreateSourceFile("bin/app.dat", GenerateData(12000));
            CreateSourceFile("readme.txt", "Hello World v1");

            await RunBuildAsync();
            PatchClient clientV1 = CreatePatchClient(backup: true, verify: true);
            PatchResult resultV1 = await clientV1.PatchAsync(_targetDir, CancellationToken.None);
            Assert.True(resultV1.Success);

            // Clean up the journal so v2 patch does not rollback v1.
            string journalPath = PatchJournal.GetJournalPath(_targetDir);
            if (File.Exists(journalPath))
            {
                File.Delete(journalPath);
            }

            // Now update source: only change readme.txt, leave others the same.
            CreateSourceFile("readme.txt", "Hello World v2");

            // Rebuild.
            CleanBuildOutput();
            await RunBuildAsync();

            // Act: patch to target that already has v1 files.
            PatchClient clientV2 = CreatePatchClient(backup: true, verify: true);
            PatchResult resultV2 = await clientV2.PatchAsync(_targetDir, CancellationToken.None);

            // Assert: only the changed file should be in AppliedFiles.
            Assert.True(resultV2.Success, $"Patch v2 failed: {resultV2.Message}");
            Assert.Single(resultV2.AppliedFiles);
            Assert.Contains("readme.txt", resultV2.AppliedFiles);

            // Verify all files are at v2 contents.
            AssertFileContentsMatch("data/config.txt");
            AssertFileContentsMatch("bin/app.dat");
            AssertFileContentsMatch("readme.txt");

            // Verify the changed file has the new content.
            string targetReadme = Path.Combine(_targetDir, "readme.txt");
            Assert.Equal("Hello World v2", File.ReadAllText(targetReadme));
        }

        [Fact]
        public async Task VerifyAsync_AfterSuccessfulPatch_AllFilesPass()
        {
            // Arrange: build and patch.
            CreateSourceFile("data/config.txt", "verification test data");
            CreateSourceFile("bin/app.dat", GenerateData(8000));

            await RunBuildAsync();
            PatchClient client = CreatePatchClient(backup: false, verify: true);
            PatchResult patchResult = await client.PatchAsync(_targetDir, CancellationToken.None);
            Assert.True(patchResult.Success);

            // Clean up journal so verify does not trigger rollback.
            string journalPath = PatchJournal.GetJournalPath(_targetDir);
            if (File.Exists(journalPath))
            {
                File.Delete(journalPath);
            }

            // Act: verify.
            PatchClient verifyClient = CreatePatchClient(backup: false, verify: true);
            VerifyResult verifyResult = await verifyClient.VerifyAsync(_targetDir, CancellationToken.None);

            // Assert.
            Assert.True(verifyResult.Success, $"Verify failed: {verifyResult.Message}");
            Assert.Empty(verifyResult.BrokenFiles);
        }

        [Fact]
        public async Task VerifyAsync_WithCorruptedFile_ReportsBrokenFile()
        {
            // Arrange: build and patch.
            CreateSourceFile("data/config.txt", "some data");
            CreateSourceFile("bin/app.dat", GenerateData(5000));

            await RunBuildAsync();
            PatchClient client = CreatePatchClient(backup: false, verify: true);
            PatchResult patchResult = await client.PatchAsync(_targetDir, CancellationToken.None);
            Assert.True(patchResult.Success);

            // Corrupt a file.
            string targetConfig = Path.Combine(_targetDir, "data", "config.txt");
            File.WriteAllText(targetConfig, "corrupted content!!!");

            // Clean up journal.
            string journalPath = PatchJournal.GetJournalPath(_targetDir);
            if (File.Exists(journalPath))
            {
                File.Delete(journalPath);
            }

            // Act.
            PatchClient verifyClient = CreatePatchClient(backup: false, verify: true);
            VerifyResult verifyResult = await verifyClient.VerifyAsync(_targetDir, CancellationToken.None);

            // Assert.
            Assert.False(verifyResult.Success);
            Assert.Contains("data/config.txt", verifyResult.BrokenFiles);
        }

        [Fact]
        public async Task RollbackAsync_AfterSuccessfulPatch_IsNoOp()
        {
            // A successful patch self-cleans the journal and backups (BUG-004 fix).
            // RollbackAsync after a successful patch should be a no-op: no journal to read,
            // so the file stays at the new (patched) version.
            Directory.CreateDirectory(Path.Combine(_targetDir, "data"));
            File.WriteAllText(Path.Combine(_targetDir, "data", "config.txt"), "original config content");

            CreateSourceFile("data/config.txt", "new updated config content");
            await RunBuildAsync();

            PatchClient client = CreatePatchClient(backup: true, verify: true);
            PatchResult patchResult = await client.PatchAsync(_targetDir, CancellationToken.None);
            Assert.True(patchResult.Success, $"Patch failed: {patchResult.Message}");

            string targetConfig = Path.Combine(_targetDir, "data", "config.txt");
            Assert.Equal("new updated config content", File.ReadAllText(targetConfig));

            // Journal and backups should already be gone after successful patch.
            string journalPath = PatchJournal.GetJournalPath(_targetDir);
            Assert.False(File.Exists(journalPath), "Journal must be cleaned up on success.");

            // Rollback is a no-op (no journal → nothing to restore).
            await client.RollbackAsync(_targetDir, CancellationToken.None);

            // File stays at patched version — rollback had nothing to act on.
            Assert.Equal("new updated config content", File.ReadAllText(targetConfig));
        }

        [Fact]
        public async Task RollbackAsync_InterruptedPatch_RestoresOriginalFiles()
        {
            // Simulate an interrupted patch by pre-seeding an InProgress journal + backup,
            // then call RollbackAsync directly and assert the original file is restored.
            Directory.CreateDirectory(Path.Combine(_targetDir, "data"));
            string originalContent = "original config content";
            string targetConfig = Path.Combine(_targetDir, "data", "config.txt");
            string backupPath = targetConfig + ".hina.bak";

            // "Patched" version is on disk; backup holds the original.
            File.WriteAllText(targetConfig, "half-patched content");
            File.WriteAllText(backupPath, originalContent);

            // Write an InProgress journal that records the backup.
            string journalPath = PatchJournal.GetJournalPath(_targetDir);
            PatchJournal staleJournal = new PatchJournal
            {
                Status = PatchJournal.StatusInProgress,
                Entries = new List<PatchJournalEntry>
                {
                    new PatchJournalEntry { TargetPath = targetConfig, BackupPath = backupPath }
                }
            };
            await staleJournal.SaveAsync(journalPath);

            // Build something (needed to create a valid patch client, even though we only call Rollback).
            CreateSourceFile("data/config.txt", "irrelevant — rollback only");
            await RunBuildAsync();

            PatchClient client = CreatePatchClient(backup: true, verify: false);

            // Act: explicit rollback restores original.
            await client.RollbackAsync(_targetDir, CancellationToken.None);

            Assert.Equal(originalContent, File.ReadAllText(targetConfig));
            Assert.False(File.Exists(journalPath), "Journal should be deleted after rollback.");
        }

        [Fact]
        public async Task CheckAsync_WhenUpToDate_ReportsNoUpdate()
        {
            // Arrange: build and patch.
            CreateSourceFile("data/config.txt", "check test content");
            CreateSourceFile("readme.txt", "readme check");
            await RunBuildAsync();

            PatchClient client = CreatePatchClient(backup: false, verify: false);
            PatchResult patchResult = await client.PatchAsync(_targetDir, CancellationToken.None);
            Assert.True(patchResult.Success);

            // Clean up journal.
            string journalPath = PatchJournal.GetJournalPath(_targetDir);
            if (File.Exists(journalPath))
            {
                File.Delete(journalPath);
            }

            // Act.
            PatchClient checkClient = CreatePatchClient(backup: false, verify: false);
            CheckResult checkResult = await checkClient.CheckAsync(_targetDir, CancellationToken.None);

            // Assert.
            Assert.False(checkResult.IsUpdateAvailable);
        }

        [Fact]
        public async Task CheckAsync_WhenFilesMissing_ReportsUpdateAvailable()
        {
            // Arrange: build but do NOT patch - target is empty.
            CreateSourceFile("data/config.txt", "missing file test");
            await RunBuildAsync();

            // Act.
            PatchClient client = CreatePatchClient(backup: false, verify: false);
            CheckResult checkResult = await client.CheckAsync(_targetDir, CancellationToken.None);

            // Assert.
            Assert.True(checkResult.IsUpdateAvailable);
        }

        [Fact]
        public async Task CheckAsync_WhenFilesOutOfDate_ReportsUpdateAvailable()
        {
            // Arrange: build and patch v1.
            CreateSourceFile("data/config.txt", "version 1");
            await RunBuildAsync();

            PatchClient clientV1 = CreatePatchClient(backup: false, verify: false);
            await clientV1.PatchAsync(_targetDir, CancellationToken.None);

            // Clean up journal.
            string journalPath = PatchJournal.GetJournalPath(_targetDir);
            if (File.Exists(journalPath))
            {
                File.Delete(journalPath);
            }

            // Rebuild with v2 content.
            CreateSourceFile("data/config.txt", "version 2");
            CleanBuildOutput();
            await RunBuildAsync();

            // Act.
            PatchClient checkClient = CreatePatchClient(backup: false, verify: false);
            CheckResult checkResult = await checkClient.CheckAsync(_targetDir, CancellationToken.None);

            // Assert.
            Assert.True(checkResult.IsUpdateAvailable);
        }

        [Fact]
        public async Task FullBuildThenPatch_LargeFile_ChunksRebuiltCorrectly()
        {
            // Test with a file that spans many chunks.
            CreateSourceFile("large.bin", GenerateData(50000));
            await RunBuildAsync();

            PatchClient client = CreatePatchClient(backup: false, verify: true);
            PatchResult result = await client.PatchAsync(_targetDir, CancellationToken.None);

            Assert.True(result.Success, $"Patch failed: {result.Message}");
            AssertFileContentsMatch("large.bin");
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
            // Deterministic pseudo-random data for reproducible tests.
            var sb = new StringBuilder(length);
            Random rng = new Random(42);
            for (int i = 0; i < length; i++)
            {
                sb.Append((char)('A' + rng.Next(0, 26)));
            }
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

            Directory.CreateDirectory(_buildOutputDir);
            await ManifestSerializer.WriteAsync(manifest, Path.Combine(_buildOutputDir, "manifest.json"), CancellationToken.None);
            await chunkWriter.WriteChunksAsync(sourceDir, chunkDir, CancellationToken.None);
        }

        private void CleanBuildOutput()
        {
            if (Directory.Exists(_buildOutputDir))
            {
                Directory.Delete(_buildOutputDir, recursive: true);
            }
            Directory.CreateDirectory(_buildOutputDir);
        }

        private PatchClient CreatePatchClient(bool backup, bool verify)
        {
            PatcherConfig config = new PatcherConfig
            {
                BaseUrl = _baseUrl,
                Channel = "stable",
                ChunkSize = ChunkSize,
                Verify = verify,
                Backup = backup,
                TrustedPublicKey = null
            };

            PatchClient client = new PatchClient(config);

            // Replace the internal HttpChunkClient with one backed by a fake handler
            // that serves manifest and chunks from the build output directory.
            var fakeHandler = new LocalChunkHandler(_buildOutputDir);
            var httpClient = new HttpClient(fakeHandler);
            var httpChunkClient = new HttpChunkClient(httpClient);

            // Use reflection to swap the private _http field.
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
            byte[] sourceBytes = File.ReadAllBytes(sourcePath);
            byte[] targetBytes = File.ReadAllBytes(targetPath);
            Assert.Equal(sourceBytes, targetBytes);
        }

        // LocalChunkHandler (fake HTTP server over the build output) lives in its own file,
        // shared with CommonMergeRoundTripTests.
    }
}
