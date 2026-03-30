using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Chunking;
using Hina.Core.Compression;
using Hina.Core.Hashing;
using Xunit;

namespace Hina.Core.Tests
{
    public class ChunkStoreWriterTests
    {
        [Fact]
        public async Task WriteChunksAsync_CreatesChunkFiles()
        {
            string tempDir = CreateTempDir();
            string srcDir = Path.Combine(tempDir, "src");
            string outDir = Path.Combine(tempDir, "chunks");
            try
            {
                Directory.CreateDirectory(srcDir);
                File.WriteAllText(Path.Combine(srcDir, "file.txt"), "0123456789abcdef");

                var hasher = new Sha256Hasher();
                var writer = new ChunkStoreWriter(hasher, 4);
                await writer.WriteChunksAsync(new DirectoryInfo(srcDir), new DirectoryInfo(outDir), CancellationToken.None);

                int chunkCount = Directory.EnumerateFiles(outDir, "*.chunk.br", SearchOption.AllDirectories).Count();
                Assert.True(chunkCount > 0);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task WriteChunksAsync_ChunksAreDecompressible()
        {
            string tempDir = CreateTempDir();
            string srcDir = Path.Combine(tempDir, "src");
            string outDir = Path.Combine(tempDir, "chunks");
            try
            {
                Directory.CreateDirectory(srcDir);
                File.WriteAllBytes(Path.Combine(srcDir, "file.txt"), System.Text.Encoding.UTF8.GetBytes("ABCD"));

                var hasher = new Sha256Hasher();
                var writer = new ChunkStoreWriter(hasher, 1024);
                await writer.WriteChunksAsync(new DirectoryInfo(srcDir), new DirectoryInfo(outDir), CancellationToken.None);

                string chunkFile = Directory.EnumerateFiles(outDir, "*.chunk.br", SearchOption.AllDirectories).First();
                byte[] compressed = File.ReadAllBytes(chunkFile);
                byte[] decompressed = BrotliCodec.Decompress(compressed);

                Assert.Equal("ABCD", System.Text.Encoding.UTF8.GetString(decompressed));
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task WriteChunksAsync_MultipleChunksForLargeFile()
        {
            string tempDir = CreateTempDir();
            string srcDir = Path.Combine(tempDir, "src");
            string outDir = Path.Combine(tempDir, "chunks");
            try
            {
                Directory.CreateDirectory(srcDir);
                File.WriteAllBytes(Path.Combine(srcDir, "file.txt"),
                    System.Text.Encoding.UTF8.GetBytes("AABBCCDDEE112233"));

                var hasher = new Sha256Hasher();
                var writer = new ChunkStoreWriter(hasher, 4);
                await writer.WriteChunksAsync(new DirectoryInfo(srcDir), new DirectoryInfo(outDir), CancellationToken.None);

                int chunkCount = Directory.EnumerateFiles(outDir, "*.chunk.br", SearchOption.AllDirectories).Count();
                Assert.True(chunkCount >= 2);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task WriteChunksAsync_DeduplicatesIdenticalChunks()
        {
            string tempDir = CreateTempDir();
            string srcDir = Path.Combine(tempDir, "src");
            string outDir = Path.Combine(tempDir, "chunks");
            try
            {
                Directory.CreateDirectory(srcDir);
                // Repeating "AAAA" with chunk size 4 means all chunks are identical
                File.WriteAllBytes(Path.Combine(srcDir, "file.txt"),
                    System.Text.Encoding.UTF8.GetBytes("AAAAAAAA"));

                var hasher = new Sha256Hasher();
                var writer = new ChunkStoreWriter(hasher, 4);
                await writer.WriteChunksAsync(new DirectoryInfo(srcDir), new DirectoryInfo(outDir), CancellationToken.None);

                int chunkCount = Directory.EnumerateFiles(outDir, "*.chunk.br", SearchOption.AllDirectories).Count();
                Assert.Equal(1, chunkCount);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task WriteChunksAsync_UsesBucketDirectories()
        {
            string tempDir = CreateTempDir();
            string srcDir = Path.Combine(tempDir, "src");
            string outDir = Path.Combine(tempDir, "chunks");
            try
            {
                Directory.CreateDirectory(srcDir);
                File.WriteAllBytes(Path.Combine(srcDir, "file.txt"),
                    System.Text.Encoding.UTF8.GetBytes("test"));

                var hasher = new Sha256Hasher();
                var writer = new ChunkStoreWriter(hasher, 1024);
                await writer.WriteChunksAsync(new DirectoryInfo(srcDir), new DirectoryInfo(outDir), CancellationToken.None);

                // Chunk files should be in a 2-char bucket subdirectory
                string chunkFile = Directory.EnumerateFiles(outDir, "*.chunk.br", SearchOption.AllDirectories).First();
                string bucketDir = Path.GetFileName(Path.GetDirectoryName(chunkFile)!);
                Assert.Equal(2, bucketDir.Length);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task WriteChunksAsync_NonExistentRoot_Throws()
        {
            string outDir = Path.Combine(Path.GetTempPath(), "hina-chunks-" + Guid.NewGuid().ToString("N"));
            string fakeRoot = Path.Combine(Path.GetTempPath(), "hina-fake-" + Guid.NewGuid().ToString("N"));

            var hasher = new Sha256Hasher();
            var writer = new ChunkStoreWriter(hasher, 4);

            await Assert.ThrowsAsync<DirectoryNotFoundException>(
                () => writer.WriteChunksAsync(new DirectoryInfo(fakeRoot), new DirectoryInfo(outDir), CancellationToken.None));
        }

        private static string CreateTempDir()
        {
            string path = Path.Combine(Path.GetTempPath(), "hina-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
