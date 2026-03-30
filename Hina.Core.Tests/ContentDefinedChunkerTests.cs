using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Hashing;
using Hina.Core.Manifest;
using Hina.Core.Rsync;
using Xunit;

namespace Hina.Core.Tests
{
    public class ContentDefinedChunkerTests
    {
        [Fact]
        public async Task ChunkAsync_ProducesVariableSizeChunks()
        {
            var hasher = new Sha256Hasher();
            var chunker = new ContentDefinedChunker(hasher, minSize: 64, maxSize: 1024, avgSize: 256);

            // Generate enough random-looking data to produce multiple variable chunks.
            byte[] data = GeneratePseudoRandom(8192, seed: 42);
            using var ms = new MemoryStream(data);
            var chunks = await chunker.ChunkAsync(ms, CancellationToken.None);

            Assert.True(chunks.Count > 1, "Expected multiple chunks");

            // At least two chunks should have different sizes (variable-size chunking).
            var distinctSizes = chunks.Select(c => c.Size).Distinct().Count();
            Assert.True(distinctSizes > 1, "Expected variable chunk sizes from CDC");
        }

        [Fact]
        public async Task ChunkAsync_RespectsMinMaxConstraints()
        {
            var hasher = new Sha256Hasher();
            int minSize = 64;
            int maxSize = 512;
            var chunker = new ContentDefinedChunker(hasher, minSize: minSize, maxSize: maxSize, avgSize: 128);

            byte[] data = GeneratePseudoRandom(16384, seed: 99);
            using var ms = new MemoryStream(data);
            var chunks = await chunker.ChunkAsync(ms, CancellationToken.None);

            Assert.True(chunks.Count > 1);

            // All chunks except possibly the last must be >= minSize.
            for (int i = 0; i < chunks.Count - 1; i++)
            {
                Assert.True(chunks[i].Size >= minSize,
                    $"Chunk {i} size {chunks[i].Size} is below minimum {minSize}");
                Assert.True(chunks[i].Size <= maxSize,
                    $"Chunk {i} size {chunks[i].Size} exceeds maximum {maxSize}");
            }

            // Last chunk can be smaller than minSize (remainder).
            Assert.True(chunks[^1].Size <= maxSize,
                $"Last chunk size {chunks[^1].Size} exceeds maximum {maxSize}");
        }

        [Fact]
        public async Task ChunkAsync_IsDeterministic()
        {
            var hasher = new Sha256Hasher();
            var chunker = new ContentDefinedChunker(hasher, minSize: 64, maxSize: 1024, avgSize: 256);

            byte[] data = GeneratePseudoRandom(4096, seed: 7);

            using var ms1 = new MemoryStream(data);
            var chunks1 = await chunker.ChunkAsync(ms1, CancellationToken.None);

            using var ms2 = new MemoryStream(data);
            var chunks2 = await chunker.ChunkAsync(ms2, CancellationToken.None);

            Assert.Equal(chunks1.Count, chunks2.Count);
            for (int i = 0; i < chunks1.Count; i++)
            {
                Assert.Equal(chunks1[i].Size, chunks2[i].Size);
                Assert.Equal(chunks1[i].Weak, chunks2[i].Weak);
                Assert.Equal(chunks1[i].Strong, chunks2[i].Strong);
                Assert.Equal(chunks1[i].Index, chunks2[i].Index);
            }
        }

        [Fact]
        public async Task ChunkAsync_DifferentInputs_DifferentBoundaries()
        {
            var hasher = new Sha256Hasher();
            var chunker = new ContentDefinedChunker(hasher, minSize: 64, maxSize: 1024, avgSize: 256);

            byte[] data1 = GeneratePseudoRandom(4096, seed: 1);
            byte[] data2 = GeneratePseudoRandom(4096, seed: 2);

            using var ms1 = new MemoryStream(data1);
            var chunks1 = await chunker.ChunkAsync(ms1, CancellationToken.None);

            using var ms2 = new MemoryStream(data2);
            var chunks2 = await chunker.ChunkAsync(ms2, CancellationToken.None);

            // The chunk boundaries (sizes) or strong hashes should differ.
            bool sameSizes = chunks1.Count == chunks2.Count &&
                chunks1.Zip(chunks2, (a, b) => a.Size == b.Size).All(x => x);
            bool sameStrong = chunks1.Count == chunks2.Count &&
                chunks1.Zip(chunks2, (a, b) => a.Strong == b.Strong).All(x => x);

            Assert.False(sameSizes && sameStrong,
                "Different inputs should produce different chunk boundaries or hashes");
        }

        [Fact]
        public async Task ChunkAsync_EmptyStream_ReturnsEmpty()
        {
            var hasher = new Sha256Hasher();
            var chunker = new ContentDefinedChunker(hasher, minSize: 64, maxSize: 1024, avgSize: 256);

            using var ms = new MemoryStream(Array.Empty<byte>());
            var chunks = await chunker.ChunkAsync(ms, CancellationToken.None);

            Assert.Empty(chunks);
        }

        [Fact]
        public async Task ChunkAsync_SmallStream_SingleChunk()
        {
            var hasher = new Sha256Hasher();
            var chunker = new ContentDefinedChunker(hasher, minSize: 64, maxSize: 1024, avgSize: 256);

            byte[] data = Encoding.UTF8.GetBytes("hello");
            using var ms = new MemoryStream(data);
            var chunks = await chunker.ChunkAsync(ms, CancellationToken.None);

            Assert.Single(chunks);
            Assert.Equal(5, chunks[0].Size);
            Assert.Equal(0, chunks[0].Index);
            Assert.StartsWith("sha256:", chunks[0].Strong);
        }

        [Fact]
        public async Task ChunkAsync_CoversEntireFile()
        {
            var hasher = new Sha256Hasher();
            var chunker = new ContentDefinedChunker(hasher, minSize: 64, maxSize: 1024, avgSize: 256);

            byte[] data = GeneratePseudoRandom(10000, seed: 123);
            using var ms = new MemoryStream(data);
            var chunks = await chunker.ChunkAsync(ms, CancellationToken.None);

            int totalSize = chunks.Sum(c => c.Size);
            Assert.Equal(data.Length, totalSize);

            // Verify indices are sequential.
            for (int i = 0; i < chunks.Count; i++)
            {
                Assert.Equal(i, chunks[i].Index);
            }
        }

        [Fact]
        public void Constructor_InvalidParameters_Throws()
        {
            var hasher = new Sha256Hasher();

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ContentDefinedChunker(hasher, minSize: 0, maxSize: 1024, avgSize: 256));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ContentDefinedChunker(hasher, minSize: 64, maxSize: 0, avgSize: 256));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ContentDefinedChunker(hasher, minSize: 64, maxSize: 1024, avgSize: 0));
            // avgSize outside min/max range
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ContentDefinedChunker(hasher, minSize: 64, maxSize: 1024, avgSize: 32));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ContentDefinedChunker(hasher, minSize: 64, maxSize: 1024, avgSize: 2048));
            // max < min
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ContentDefinedChunker(hasher, minSize: 1024, maxSize: 64, avgSize: 64));
        }

        [Fact]
        public async Task ChunkAsync_WeakHashesMatchRollingChecksum()
        {
            var hasher = new Sha256Hasher();
            var chunker = new ContentDefinedChunker(hasher, minSize: 64, maxSize: 1024, avgSize: 256);

            byte[] data = GeneratePseudoRandom(2048, seed: 55);
            using var ms = new MemoryStream(data);
            var chunks = await chunker.ChunkAsync(ms, CancellationToken.None);

            // Verify each chunk's weak hash matches direct computation.
            int offset = 0;
            foreach (var chunk in chunks)
            {
                uint expected = RollingChecksum.Compute(data.AsSpan(offset, chunk.Size));
                Assert.Equal(expected, chunk.Weak);
                offset += chunk.Size;
            }
        }

        private static byte[] GeneratePseudoRandom(int length, int seed)
        {
            var rng = new Random(seed);
            byte[] data = new byte[length];
            rng.NextBytes(data);
            return data;
        }
    }
}
