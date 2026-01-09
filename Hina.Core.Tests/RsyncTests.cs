using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Chunking;
using Hina.Core.Hashing;
using Hina.Core.Manifest;
using Hina.Core.Rsync;
using Xunit;

namespace Hina.Core.Tests
{
    public class RsyncTests
    {
        [Fact]
        public void RollingChecksum_RollMatchesRecompute()
        {
            // Verify rolling checksum equals a full recompute on the next window.
            byte[] data = Encoding.ASCII.GetBytes("abcdefghijklmnopqrstuvwxyz");
            int window = 8;

            uint w1 = RollingChecksum.Compute(data.AsSpan(0, window));
            uint w2 = RollingChecksum.Roll(w1, data[0], data[window], window);
            uint recompute = RollingChecksum.Compute(data.AsSpan(1, window));

            Assert.Equal(recompute, w2);
        }

        [Fact]
        public async Task ManifestBuilder_BuildsManifest()
        {
            // Create a small file set and ensure manifest is consistent.
            string tempDir = CreateTempDir();
            try
            {
                Directory.CreateDirectory(Path.Combine(tempDir, "bin"));
                File.WriteAllText(Path.Combine(tempDir, "bin", "a.txt"), "hello");
                File.WriteAllText(Path.Combine(tempDir, "b.txt"), "world");

                IHasher hasher = new Sha256Hasher();
                ManifestBuilder builder = new ManifestBuilder(hasher);
                Manifest.Manifest manifest = await builder.BuildAsync(
                    new DirectoryInfo(tempDir),
                    new Uri("http://localhost/"),
                    4,
                    CancellationToken.None);

                Assert.Equal(2, manifest.Files.Count);
                Assert.All(manifest.Files, f => Assert.StartsWith("sha256:", f.FileHash, StringComparison.OrdinalIgnoreCase));
                Assert.All(manifest.Files, f => Assert.Equal(4, f.ChunkSize));
                Assert.Contains(manifest.Files, f => f.Path == "bin/a.txt");
                Assert.Contains(manifest.Files, f => f.Path == "b.txt");
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task ChunkStoreWriter_WritesChunks()
        {
            // Ensure chunk store is populated on disk.
            string tempDir = CreateTempDir();
            string outDir = Path.Combine(tempDir, "chunks");
            try
            {
                File.WriteAllText(Path.Combine(tempDir, "file.txt"), "0123456789abcdef");

                IHasher hasher = new Sha256Hasher();
                ChunkStoreWriter writer = new ChunkStoreWriter(hasher, 4);
                await writer.WriteChunksAsync(new DirectoryInfo(tempDir), new DirectoryInfo(outDir), CancellationToken.None);

                int chunkFiles = Directory.EnumerateFiles(outDir, "*.chunk.br", SearchOption.AllDirectories).Count();
                Assert.True(chunkFiles > 0);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task ManifestSigner_SignsAndVerifies()
        {
            // Sign a manifest and verify with the included public key.
            string tempDir = CreateTempDir();
            try
            {
                File.WriteAllText(Path.Combine(tempDir, "a.txt"), "hello");
                IHasher hasher = new Sha256Hasher();
                ManifestBuilder builder = new ManifestBuilder(hasher);
                Manifest.Manifest manifest = await builder.BuildAsync(
                    new DirectoryInfo(tempDir),
                    new Uri("http://localhost/"),
                    4,
                    CancellationToken.None);

                byte[] privateKey = new byte[32];
                System.Security.Cryptography.RandomNumberGenerator.Fill(privateKey);

                ManifestSigner.AttachSignature(manifest, privateKey);
                Assert.NotNull(manifest.Signature);
                bool ok = ManifestSigner.Verify(manifest, manifest.Signature!.PublicKey);
                Assert.True(ok);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void PatchCleanup_RemovesArtifacts()
        {
            string tempDir = CreateTempDir();
            try
            {
                string journalDir = Path.Combine(tempDir, ".hina");
                Directory.CreateDirectory(journalDir);
                File.WriteAllText(Path.Combine(journalDir, "journal.json"), "{}");
                File.WriteAllText(Path.Combine(tempDir, "file.hina.tmp"), "tmp");
                File.WriteAllText(Path.Combine(tempDir, "file.hina.bak"), "bak");

                Hina.Core.Patching.PatchCleanup.Cleanup(tempDir);

                Assert.False(File.Exists(Path.Combine(journalDir, "journal.json")));
                Assert.False(File.Exists(Path.Combine(tempDir, "file.hina.tmp")));
                Assert.False(File.Exists(Path.Combine(tempDir, "file.hina.bak")));
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        private static string CreateTempDir()
        {
            string path = Path.Combine(Path.GetTempPath(), "hina-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
