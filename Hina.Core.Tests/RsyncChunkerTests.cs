using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Hashing;
using Hina.Core.Manifest;
using Hina.Core.Rsync;
using Xunit;

namespace Hina.Core.Tests
{
    public class RsyncChunkerTests
    {
        [Fact]
        public void Constructor_InvalidChunkSize_Throws()
        {
            var hasher = new Sha256Hasher();
            Assert.Throws<ArgumentOutOfRangeException>(() => new RsyncChunker(0, hasher));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RsyncChunker(-1, hasher));
        }

        [Fact]
        public async Task BuildChunksAsync_SingleChunk()
        {
            var hasher = new Sha256Hasher();
            var chunker = new RsyncChunker(1024, hasher);

            using var ms = new MemoryStream(Encoding.UTF8.GetBytes("hello"));
            var chunks = await chunker.BuildChunksAsync(ms, CancellationToken.None);

            Assert.Single(chunks);
            Assert.Equal(0, chunks[0].Index);
            Assert.Equal(5, chunks[0].Size);
            Assert.StartsWith("sha256:", chunks[0].Strong);
            Assert.NotEqual(0u, chunks[0].Weak);
        }

        [Fact]
        public async Task BuildChunksAsync_MultipleChunks()
        {
            var hasher = new Sha256Hasher();
            var chunker = new RsyncChunker(4, hasher);

            // 12 bytes = 3 chunks of 4
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes("aabbccddee12"));
            var chunks = await chunker.BuildChunksAsync(ms, CancellationToken.None);

            Assert.Equal(3, chunks.Count);
            Assert.Equal(0, chunks[0].Index);
            Assert.Equal(1, chunks[1].Index);
            Assert.Equal(2, chunks[2].Index);
            Assert.All(chunks, c => Assert.Equal(4, c.Size));
        }

        [Fact]
        public async Task BuildChunksAsync_LastChunkCanBeSmaller()
        {
            var hasher = new Sha256Hasher();
            var chunker = new RsyncChunker(4, hasher);

            // 7 bytes = chunk of 4 + chunk of 3
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes("ABCDEFG"));
            var chunks = await chunker.BuildChunksAsync(ms, CancellationToken.None);

            Assert.Equal(2, chunks.Count);
            Assert.Equal(4, chunks[0].Size);
            Assert.Equal(3, chunks[1].Size);
        }

        [Fact]
        public async Task BuildChunksAsync_EmptyStream_ReturnsEmpty()
        {
            var hasher = new Sha256Hasher();
            var chunker = new RsyncChunker(4, hasher);

            using var ms = new MemoryStream(Array.Empty<byte>());
            var chunks = await chunker.BuildChunksAsync(ms, CancellationToken.None);

            Assert.Empty(chunks);
        }

        [Fact]
        public async Task BuildChunksAsync_WeakChecksumsMatchRollingChecksum()
        {
            var hasher = new Sha256Hasher();
            var chunker = new RsyncChunker(4, hasher);

            byte[] data = Encoding.UTF8.GetBytes("ABCDEFGH");
            using var ms = new MemoryStream(data);
            var chunks = await chunker.BuildChunksAsync(ms, CancellationToken.None);

            // Verify weak checksums match direct computation
            uint weak0 = RollingChecksum.Compute(data.AsSpan(0, 4));
            uint weak1 = RollingChecksum.Compute(data.AsSpan(4, 4));

            Assert.Equal(weak0, chunks[0].Weak);
            Assert.Equal(weak1, chunks[1].Weak);
        }

        [Fact]
        public async Task BuildChunksAsync_StrongHashIsDeterministic()
        {
            var hasher = new Sha256Hasher();
            var chunker = new RsyncChunker(1024, hasher);
            byte[] data = Encoding.UTF8.GetBytes("deterministic");

            using var ms1 = new MemoryStream(data);
            var chunks1 = await chunker.BuildChunksAsync(ms1, CancellationToken.None);

            using var ms2 = new MemoryStream(data);
            var chunks2 = await chunker.BuildChunksAsync(ms2, CancellationToken.None);

            Assert.Equal(chunks1[0].Strong, chunks2[0].Strong);
            Assert.Equal(chunks1[0].Weak, chunks2[0].Weak);
        }

        [Fact]
        public async Task BuildChunksAsync_DifferentContent_DifferentHashes()
        {
            var hasher = new Sha256Hasher();
            var chunker = new RsyncChunker(1024, hasher);

            using var ms1 = new MemoryStream(Encoding.UTF8.GetBytes("content_a"));
            var chunks1 = await chunker.BuildChunksAsync(ms1, CancellationToken.None);

            using var ms2 = new MemoryStream(Encoding.UTF8.GetBytes("content_b"));
            var chunks2 = await chunker.BuildChunksAsync(ms2, CancellationToken.None);

            Assert.NotEqual(chunks1[0].Strong, chunks2[0].Strong);
        }
    }
}
