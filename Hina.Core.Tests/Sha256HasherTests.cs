using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Hashing;
using Xunit;

namespace Hina.Core.Tests
{
    public class Sha256HasherTests
    {
        [Fact]
        public async Task AlgorithmId_IsSha256()
        {
            var hasher = new Sha256Hasher();
            Assert.Equal("sha256", hasher.AlgorithmId);
        }

        [Fact]
        public async Task ComputeHashAsync_ReturnsCorrectHash()
        {
            var hasher = new Sha256Hasher();
            byte[] data = Encoding.UTF8.GetBytes("hello");
            using var ms = new MemoryStream(data);

            string hash = await hasher.ComputeHashAsync(ms, CancellationToken.None);

            // Known SHA256 of "hello"
            Assert.Equal("sha256:2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", hash);
        }

        [Fact]
        public async Task ComputeHashAsync_EmptyStream_ReturnsEmptyHash()
        {
            var hasher = new Sha256Hasher();
            using var ms = new MemoryStream(System.Array.Empty<byte>());

            string hash = await hasher.ComputeHashAsync(ms, CancellationToken.None);

            // SHA256 of empty input
            Assert.Equal("sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", hash);
        }

        [Fact]
        public async Task ComputeHashAsync_SameInput_SameOutput()
        {
            var hasher = new Sha256Hasher();
            byte[] data = Encoding.UTF8.GetBytes("deterministic");

            using var ms1 = new MemoryStream(data);
            string hash1 = await hasher.ComputeHashAsync(ms1, CancellationToken.None);

            using var ms2 = new MemoryStream(data);
            string hash2 = await hasher.ComputeHashAsync(ms2, CancellationToken.None);

            Assert.Equal(hash1, hash2);
        }

        [Fact]
        public async Task ComputeHashAsync_DifferentInput_DifferentOutput()
        {
            var hasher = new Sha256Hasher();

            using var ms1 = new MemoryStream(Encoding.UTF8.GetBytes("aaa"));
            string hash1 = await hasher.ComputeHashAsync(ms1, CancellationToken.None);

            using var ms2 = new MemoryStream(Encoding.UTF8.GetBytes("bbb"));
            string hash2 = await hasher.ComputeHashAsync(ms2, CancellationToken.None);

            Assert.NotEqual(hash1, hash2);
        }

        [Fact]
        public async Task ComputeHashAsync_StartsWithPrefix()
        {
            var hasher = new Sha256Hasher();
            using var ms = new MemoryStream(new byte[] { 1, 2, 3 });

            string hash = await hasher.ComputeHashAsync(ms, CancellationToken.None);

            Assert.StartsWith("sha256:", hash);
            // Hex portion should be 64 chars (32 bytes)
            string hex = hash.Substring("sha256:".Length);
            Assert.Equal(64, hex.Length);
            Assert.Matches("^[0-9a-f]+$", hex);
        }
    }
}
