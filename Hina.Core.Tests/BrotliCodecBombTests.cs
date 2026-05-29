using System.IO;
using System.Text;
using Hina.Core.Compression;
using Xunit;

namespace Hina.Core.Tests
{
    public class BrotliCodecBombTests
    {
        [Fact]
        public void Decompress_ExceedingMaxBytes_Throws()
        {
            // 1 MB of zeros compresses to a few bytes — the classic decompression bomb shape.
            byte[] compressed = BrotliCodec.Compress(new byte[1_000_000]);

            Assert.Throws<InvalidDataException>(() => BrotliCodec.Decompress(compressed, maxBytes: 4096));
        }

        [Fact]
        public void Decompress_WithinMaxBytes_RoundTrips()
        {
            byte[] data = Encoding.UTF8.GetBytes("hello world");
            byte[] result = BrotliCodec.Decompress(BrotliCodec.Compress(data), maxBytes: 4096);
            Assert.Equal(data, result);
        }

        [Fact]
        public void Decompress_DefaultCap_RoundTripsModerateData()
        {
            byte[] data = new byte[200_000];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 31);
            byte[] result = BrotliCodec.Decompress(BrotliCodec.Compress(data));
            Assert.Equal(data, result);
        }
    }
}
