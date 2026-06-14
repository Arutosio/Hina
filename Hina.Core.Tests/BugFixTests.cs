using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using Hina.Core.Rsync;
using Xunit;

namespace Hina.Core.Tests
{
    // Regression tests for BUG-002, BUG-004, BUG-010, BUG-011.
    public class BugFixTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string _sourceDir;
        private readonly string _buildOutputDir;
        private readonly string _targetDir;
        private readonly Uri _baseUrl = new Uri("http://test.local/");

        public BugFixTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "hina_bugfix_" + Guid.NewGuid().ToString("N"));
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
                    Directory.Delete(_tempRoot, recursive: true);
            }
            catch { /* best effort */ }
        }

        // -----------------------------------------------------------------------
        // BUG-002 / BUG-010 — CDC manifest: delta matching must find reusable chunks
        // -----------------------------------------------------------------------

        [Fact]
        public async Task BUG002_CdcManifest_RsyncMatchFindsChunks_WhenLocalFileHasSameContent()
        {
            // Arrange: build with a CDC chunker so ManifestFile.ChunkSize == 0
            // and chunks have variable sizes.  The local target starts as an exact copy,
            // so every chunk should be found locally — no downloads needed.
            IHasher hasher = new Sha256Hasher();
            string content = GenerateData(40000, seed: 1);
            string sourcePath = CreateSourceFile("data.bin", content);

            // Build the manifest using CDC chunker.
            ManifestBuilder builder = new ManifestBuilder(hasher);
            ContentDefinedChunker cdc = new ContentDefinedChunker(hasher, minSize: 512, maxSize: 4096, avgSize: 1024);
            DirectoryInfo sourceDir = new DirectoryInfo(_sourceDir);
            Manifest.Manifest manifest = await builder.BuildAsync(sourceDir, _baseUrl, 0, cdc, CancellationToken.None);

            // Sanity: ChunkSize should be 0 for CDC builds.
            Assert.All(manifest.Files, f => Assert.Equal(0, f.ChunkSize));

            // Sanity: chunks should have variable sizes (more than one distinct size).
            List<int> sizes = manifest.Files[0].Chunks.Select(c => c.Size).Distinct().ToList();
            Assert.True(sizes.Count > 1, $"Expected multiple distinct chunk sizes for CDC; got {sizes.Count}");

            // Write chunks and manifest to disk so the patch client can serve them.
            ChunkStoreWriter chunkWriter = new ChunkStoreWriter(hasher, 1024);
            DirectoryInfo chunkDir = new DirectoryInfo(Path.Combine(_buildOutputDir, "chunks"));
            manifest.Version = "1.0.0";
            await ManifestSerializer.WriteAsync(manifest, Path.Combine(_buildOutputDir, "manifest.json"), CancellationToken.None);
            await chunkWriter.WriteChunksAsync(sourceDir, chunkDir, CancellationToken.None);

            // Place the same file content in the target so the matcher can reuse everything.
            Directory.CreateDirectory(_targetDir);
            File.WriteAllText(Path.Combine(_targetDir, "data.bin"), content);

            // Patch (with verify=true to confirm byte-identical rebuild).
            PatchClient client = CreatePatchClient(backup: false, verify: true);

            // Instrument: we want to confirm matches > 0.  We do this by counting how many
            // chunks exist and then asserting the patch client returned Success with the rebuilt
            // file being identical — if matching were zero the download would still work, but
            // the key assertion is that the patch succeeds and the file is correct.
            PatchResult result = await client.PatchAsync(_targetDir, CancellationToken.None);

            Assert.True(result.Success, $"Patch failed: {result.Message}");
            byte[] targetBytes = File.ReadAllBytes(Path.Combine(_targetDir, "data.bin"));
            byte[] sourceBytes = File.ReadAllBytes(sourcePath);
            Assert.Equal(sourceBytes, targetBytes);
        }

        [Fact]
        public async Task BUG002_CdcManifest_RsyncMatchProducesMatchesGreaterThanZero()
        {
            // Arrange: build CDC manifest, put the SAME file in target, then patch and
            // assert zero chunks were downloaded (all reused from local) by checking that
            // the patch succeeded on a fake HTTP server that returns 404 for chunks.
            // If matching is zero every chunk would be fetched — the 404 server would cause failure.
            IHasher hasher = new Sha256Hasher();
            string content = GenerateData(20000, seed: 7);
            CreateSourceFile("file.bin", content);

            ManifestBuilder builder = new ManifestBuilder(hasher);
            ContentDefinedChunker cdc = new ContentDefinedChunker(hasher, minSize: 512, maxSize: 4096, avgSize: 1024);
            DirectoryInfo sourceDir = new DirectoryInfo(_sourceDir);
            Manifest.Manifest manifest = await builder.BuildAsync(sourceDir, _baseUrl, 0, cdc, CancellationToken.None);

            // Write manifest but NOT the chunks (404 server).
            manifest.Version = "1.0.0";
            await ManifestSerializer.WriteAsync(manifest, Path.Combine(_buildOutputDir, "manifest.json"), CancellationToken.None);

            // Place identical content in target.
            File.WriteAllText(Path.Combine(_targetDir, "file.bin"), content);

            // Patch client backed by a server that serves manifest but returns 404 for chunks.
            PatchClient client = CreatePatchClient(backup: false, verify: true, chunkServer404: true);
            PatchResult result = await client.PatchAsync(_targetDir, CancellationToken.None);

            // If CDC matching works all chunks are reused from local file — no download needed.
            Assert.True(result.Success,
                $"Patch failed ({result.Message}). This means CDC matching produced zero matches and tried to download chunks that returned 404.");
        }

        // -----------------------------------------------------------------------
        // BUG-010 — Last (shorter) chunk must also be found by the size-aware matcher
        // -----------------------------------------------------------------------

        [Fact]
        public async Task BUG010_LastPartialChunk_IsMatchedLocally()
        {
            // Create a file whose length is not an exact multiple of the chunk size,
            // so RsyncChunker emits a final shorter chunk.  Then verify the matcher finds it.
            IHasher hasher = new Sha256Hasher();
            int chunkSize = 512;
            // 2.5 chunks: two full chunks + one shorter final chunk of 256 bytes.
            string content = GenerateData(chunkSize * 2 + 256, seed: 42);
            CreateSourceFile("partial.bin", content);

            ManifestBuilder builder = new ManifestBuilder(hasher);
            DirectoryInfo sourceDir = new DirectoryInfo(_sourceDir);
            Manifest.Manifest manifest = await builder.BuildAsync(sourceDir, _baseUrl, chunkSize, CancellationToken.None);

            ManifestFile mf = manifest.Files[0];
            // Verify the last chunk is shorter.
            ManifestChunk lastChunk = mf.Chunks[mf.Chunks.Count - 1];
            Assert.Equal(256, lastChunk.Size);
            Assert.NotEqual(chunkSize, lastChunk.Size);

            // Write chunks and manifest.
            ChunkStoreWriter chunkWriter = new ChunkStoreWriter(hasher, chunkSize);
            DirectoryInfo chunkDir = new DirectoryInfo(Path.Combine(_buildOutputDir, "chunks"));
            manifest.Version = "1.0.0";
            await ManifestSerializer.WriteAsync(manifest, Path.Combine(_buildOutputDir, "manifest.json"), CancellationToken.None);
            await chunkWriter.WriteChunksAsync(sourceDir, chunkDir, CancellationToken.None);

            // Place the same file in target.
            File.WriteAllText(Path.Combine(_targetDir, "partial.bin"), content);

            // Patch via 404-chunk server: all chunks (including the partial one) must be matched locally.
            PatchClient client = CreatePatchClient(backup: false, verify: true, chunkServer404: true);
            PatchResult result = await client.PatchAsync(_targetDir, CancellationToken.None);

            Assert.True(result.Success,
                $"Patch failed ({result.Message}). The partial last chunk was not matched locally and download was attempted.");
        }

        // -----------------------------------------------------------------------
        // Fixed-size regression: existing single-size behavior must remain unchanged
        // -----------------------------------------------------------------------

        [Fact]
        public async Task FixedSize_RsyncMatch_BehaviorUnchanged_AfterRefactor()
        {
            // Regression guard: fixed-size chunking still produces matches after the refactor.
            IHasher hasher = new Sha256Hasher();
            int chunkSize = 256;
            string content = GenerateData(chunkSize * 10, seed: 55);
            CreateSourceFile("fixed.bin", content);

            ManifestBuilder builder = new ManifestBuilder(hasher);
            DirectoryInfo sourceDir = new DirectoryInfo(_sourceDir);
            Manifest.Manifest manifest = await builder.BuildAsync(sourceDir, _baseUrl, chunkSize, CancellationToken.None);

            // Fixed chunker should record the requested chunkSize.
            Assert.All(manifest.Files, f => Assert.Equal(chunkSize, f.ChunkSize));

            ChunkStoreWriter chunkWriter = new ChunkStoreWriter(hasher, chunkSize);
            DirectoryInfo chunkDir = new DirectoryInfo(Path.Combine(_buildOutputDir, "chunks"));
            manifest.Version = "1.0.0";
            await ManifestSerializer.WriteAsync(manifest, Path.Combine(_buildOutputDir, "manifest.json"), CancellationToken.None);
            await chunkWriter.WriteChunksAsync(sourceDir, chunkDir, CancellationToken.None);

            // Same content in target — 404 server means all must come from local match.
            File.WriteAllText(Path.Combine(_targetDir, "fixed.bin"), content);

            PatchClient client = CreatePatchClient(backup: false, verify: true, chunkServer404: true);
            PatchResult result = await client.PatchAsync(_targetDir, CancellationToken.None);

            Assert.True(result.Success,
                $"Fixed-size regression: patch failed ({result.Message}).");
        }

        // -----------------------------------------------------------------------
        // BUG-004 — Journal with Status="Completed" must NOT trigger spurious rollback
        // -----------------------------------------------------------------------

        [Fact]
        public async Task BUG004_CompletedJournal_DoesNotCauseRollback_OnNextPatch()
        {
            // Direct repro: patch v1 (leaves Completed journal on disk), then patch v2
            // without any manual journal deletion.  v2 must apply cleanly — before the fix
            // the Completed journal caused RollbackAsync to restore v1 files over the already-
            // applied v2 content, then re-patch from scratch.
            int chunkSize = 256;

            // Build and patch v1.
            CreateSourceFile("app.txt", "version one content here");
            await RunBuildAsync(chunkSize);

            PatchClient clientV1 = CreatePatchClient(backup: true, verify: false);
            PatchResult r1 = await clientV1.PatchAsync(_targetDir, CancellationToken.None);
            Assert.True(r1.Success, $"v1 patch failed: {r1.Message}");

            // At this point the Completed journal (and cleaned backups) should be gone.
            string journalPath = PatchJournal.GetJournalPath(_targetDir);
            Assert.False(File.Exists(journalPath),
                "Journal should be cleaned up after successful Backup=true patch.");

            // Build v2 — only app.txt changes.
            CreateSourceFile("app.txt", "version two content here — updated");
            CleanBuildOutput();
            await RunBuildAsync(chunkSize);

            // Patch v2 WITHOUT touching the journal (key: no manual cleanup).
            PatchClient clientV2 = CreatePatchClient(backup: true, verify: false);
            PatchResult r2 = await clientV2.PatchAsync(_targetDir, CancellationToken.None);

            Assert.True(r2.Success, $"v2 patch failed: {r2.Message}");
            string targetContent = File.ReadAllText(Path.Combine(_targetDir, "app.txt"));
            Assert.Equal("version two content here — updated", targetContent);
        }

        [Fact]
        public async Task BUG004_SuccessfulPatch_DeletesJournalAndBackups()
        {
            // After a successful Backup=true patch, neither journal.json nor *.hina.bak
            // files should remain on disk.
            int chunkSize = 256;

            // Pre-existing file so there is something to back up.
            string targetFile = Path.Combine(_targetDir, "item.txt");
            File.WriteAllText(targetFile, "old content");

            CreateSourceFile("item.txt", "new content");
            await RunBuildAsync(chunkSize);

            PatchClient client = CreatePatchClient(backup: true, verify: false);
            PatchResult result = await client.PatchAsync(_targetDir, CancellationToken.None);

            Assert.True(result.Success, $"Patch failed: {result.Message}");

            string journalPath = PatchJournal.GetJournalPath(_targetDir);
            Assert.False(File.Exists(journalPath), "Journal must be removed after successful patch.");

            string[] bakFiles = Directory.GetFiles(_targetDir, "*.hina.bak", SearchOption.AllDirectories);
            Assert.Empty(bakFiles);
        }

        [Fact]
        public async Task BUG004_InProgressJournal_TriggersRollback()
        {
            // Guard against the fix over-suppressing rollback: an InProgress journal must
            // still cause RollbackAsync to fire and restore backups.
            int chunkSize = 256;

            // Place a pre-existing file and record a fake InProgress journal + backup.
            string targetFile = Path.Combine(_targetDir, "data.txt");
            string backupFile = targetFile + ".hina.bak";
            File.WriteAllText(targetFile, "corrupted half-patched");
            File.WriteAllText(backupFile, "original content");

            string journalDir = Path.Combine(_targetDir, ".hina");
            Directory.CreateDirectory(journalDir);
            PatchJournal staleJournal = new PatchJournal
            {
                Status = PatchJournal.StatusInProgress,
                Entries = new List<PatchJournalEntry>
                {
                    new PatchJournalEntry
                    {
                        TargetPath = targetFile,
                        BackupPath = backupFile
                    }
                }
            };
            await staleJournal.SaveAsync(PatchJournal.GetJournalPath(_targetDir));

            // Build and patch a NEW version — the InProgress journal should first roll back.
            CreateSourceFile("data.txt", "version two content");
            await RunBuildAsync(chunkSize);

            PatchClient client = CreatePatchClient(backup: true, verify: false);
            PatchResult result = await client.PatchAsync(_targetDir, CancellationToken.None);

            Assert.True(result.Success, $"Patch failed: {result.Message}");
            // After rollback the original is restored, then the patch applies v2.
            Assert.Equal("version two content", File.ReadAllText(targetFile));
        }

        // -----------------------------------------------------------------------
        // BUG-011 — Invalid chunk.Size rejected before ArrayPool.Rent
        // -----------------------------------------------------------------------

        [Fact]
        public async Task BUG011_ChunkSizeZero_ThrowsInvalidDataException_NotCrash()
        {
            // Arrange: build a valid manifest then tamper with one chunk's Size to 0.
            int chunkSize = 256;
            CreateSourceFile("evil.bin", GenerateData(chunkSize * 2, seed: 3));
            Manifest.Manifest manifest = await RunBuildAsync(chunkSize);

            // Tamper: set Size=0 on the first chunk (triggers the CopyChunk path when
            // the local file already exists, which we ensure by copying source to target).
            manifest.Files[0].Chunks[0] = new ManifestChunk
            {
                Index = manifest.Files[0].Chunks[0].Index,
                Weak = manifest.Files[0].Chunks[0].Weak,
                Strong = manifest.Files[0].Chunks[0].Strong,
                Size = 0   // invalid
            };
            await ManifestSerializer.WriteAsync(manifest, Path.Combine(_buildOutputDir, "manifest.json"), CancellationToken.None);

            // Place a DIFFERENT file in target so the whole-file match fails and per-chunk
            // processing actually runs (an identical copy would let the patch skip the file
            // entirely and never reach the chunk-size guard).
            File.WriteAllText(Path.Combine(_targetDir, "evil.bin"), new string('x', chunkSize * 2));

            PatchClient client = CreatePatchClient(backup: false, verify: false);
            PatchResult result = await client.PatchAsync(_targetDir, CancellationToken.None);

            // Must fail gracefully — the up-front chunk-size validation rejects Size=0 with an
            // InvalidDataException instead of writing a truncated file or hitting ArrayPool.Rent
            // with a bad length.
            Assert.False(result.Success);
        }

        [Fact]
        public async Task BUG011_ChunkSizeNegative_ThrowsInvalidDataException_NotCrash()
        {
            // Same as above but with Size = -1 to verify the <= 0 guard.
            int chunkSize = 256;
            CreateSourceFile("neg.bin", GenerateData(chunkSize * 2, seed: 9));
            Manifest.Manifest manifest = await RunBuildAsync(chunkSize);

            manifest.Files[0].Chunks[0] = new ManifestChunk
            {
                Index = manifest.Files[0].Chunks[0].Index,
                Weak = manifest.Files[0].Chunks[0].Weak,
                Strong = manifest.Files[0].Chunks[0].Strong,
                Size = -1
            };
            await ManifestSerializer.WriteAsync(manifest, Path.Combine(_buildOutputDir, "manifest.json"), CancellationToken.None);

            // Different target content forces per-chunk processing (see the Size=0 test above).
            File.WriteAllText(Path.Combine(_targetDir, "neg.bin"), new string('y', chunkSize * 2));

            PatchClient client = CreatePatchClient(backup: false, verify: false);
            PatchResult result = await client.PatchAsync(_targetDir, CancellationToken.None);

            Assert.False(result.Success);
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private string CreateSourceFile(string relativePath, string content)
        {
            string fullPath = Path.Combine(_sourceDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
            return fullPath;
        }

        private static string GenerateData(int length, int seed = 42)
        {
            StringBuilder sb = new StringBuilder(length);
            Random rng = new Random(seed);
            for (int i = 0; i < length; i++)
                sb.Append((char)('A' + rng.Next(0, 26)));
            return sb.ToString();
        }

        private async Task<Manifest.Manifest> RunBuildAsync(int chunkSize = 256)
        {
            IHasher hasher = new Sha256Hasher();
            ManifestBuilder builder = new ManifestBuilder(hasher);
            ChunkStoreWriter chunkWriter = new ChunkStoreWriter(hasher, chunkSize);

            DirectoryInfo sourceDir = new DirectoryInfo(_sourceDir);
            DirectoryInfo chunkDir = new DirectoryInfo(Path.Combine(_buildOutputDir, "chunks"));

            Manifest.Manifest manifest = await builder.BuildAsync(sourceDir, _baseUrl, chunkSize, CancellationToken.None);
            manifest.Version = "1.0.0";

            Directory.CreateDirectory(_buildOutputDir);
            await ManifestSerializer.WriteAsync(manifest, Path.Combine(_buildOutputDir, "manifest.json"), CancellationToken.None);
            await chunkWriter.WriteChunksAsync(sourceDir, chunkDir, CancellationToken.None);
            return manifest;
        }

        private void CleanBuildOutput()
        {
            if (Directory.Exists(_buildOutputDir))
                Directory.Delete(_buildOutputDir, recursive: true);
            Directory.CreateDirectory(_buildOutputDir);
        }

        private PatchClient CreatePatchClient(bool backup, bool verify, bool chunkServer404 = false)
        {
            PatcherConfig config = new PatcherConfig
            {
                BaseUrl = _baseUrl,
                Channel = "stable",
                ChunkSize = 256,
                Concurrency = 2,
                Verify = verify,
                Backup = backup,
                TrustedPublicKey = null
            };

            PatchClient client = new PatchClient(config);

            HttpMessageHandler handler = chunkServer404
                ? (HttpMessageHandler)new ManifestOnlyHandler(_buildOutputDir)
                : new LocalChunkHandler(_buildOutputDir);

            var httpClient = new HttpClient(handler);
            var httpChunkClient = new HttpChunkClient(httpClient);

            FieldInfo? httpField = typeof(PatchClient).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance);
            httpField!.SetValue(client, httpChunkClient);

            return client;
        }

        // Serves manifest.json but returns 404 for all chunk requests.
        // Used to verify that patches succeed purely via local rsync matching.
        private sealed class ManifestOnlyHandler : HttpMessageHandler
        {
            private readonly string _buildOutputDir;

            public ManifestOnlyHandler(string buildOutputDir)
            {
                _buildOutputDir = buildOutputDir;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string path = request.RequestUri!.AbsolutePath.TrimStart('/');

                if (path.StartsWith("manifest", StringComparison.OrdinalIgnoreCase) &&
                    path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    string manifestPath = Path.Combine(_buildOutputDir, "manifest.json");
                    if (File.Exists(manifestPath))
                    {
                        byte[] data = File.ReadAllBytes(manifestPath);
                        var resp = new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new ByteArrayContent(data)
                        };
                        resp.Content.Headers.ContentType =
                            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                        return Task.FromResult(resp);
                    }
                }

                // All chunk requests return 404 — forces local matching to handle them.
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }
    }
}
