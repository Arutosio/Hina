using System;
using System.Text;
using Hina.Core.Compression;
using Xunit;

namespace Hina.Core.Tests
{
    public class BrotliCodecTests
    {
        [Fact]
        public void Compress_Decompress_RoundTrip()
        {
            byte[] original = Encoding.UTF8.GetBytes("Hello, Hina patcher! This is a test of Brotli compression.");
            byte[] compressed = BrotliCodec.Compress(original);
            byte[] decompressed = BrotliCodec.Decompress(compressed);

            Assert.Equal(original, decompressed);
        }

        [Fact]
        public void Compress_ProducesSmallerOutput_ForRepetitiveData()
        {
            // Highly repetitive data should compress well.
            byte[] data = Encoding.UTF8.GetBytes(new string('A', 10_000));
            byte[] compressed = BrotliCodec.Compress(data);

            Assert.True(compressed.Length < data.Length,
                $"Expected compressed ({compressed.Length}) < original ({data.Length})");
        }

        [Fact]
        public void Compress_Decompress_EmptyArray()
        {
            byte[] empty = Array.Empty<byte>();
            byte[] compressed = BrotliCodec.Compress(empty);
            byte[] decompressed = BrotliCodec.Decompress(compressed);

            Assert.Empty(decompressed);
        }

        [Fact]
        public void Compress_Decompress_SingleByte()
        {
            byte[] single = new byte[] { 0x42 };
            byte[] compressed = BrotliCodec.Compress(single);
            byte[] decompressed = BrotliCodec.Decompress(compressed);

            Assert.Equal(single, decompressed);
        }

        [Fact]
        public void Compress_Decompress_BinaryData()
        {
            // Test with random-ish binary data.
            byte[] data = new byte[4096];
            new Random(42).NextBytes(data);

            byte[] compressed = BrotliCodec.Compress(data);
            byte[] decompressed = BrotliCodec.Decompress(compressed);

            Assert.Equal(data, decompressed);
        }

        [Fact]
        public void Decompress_InvalidData_Throws()
        {
            byte[] garbage = new byte[] { 0xFF, 0xFE, 0xFD, 0xFC };
            Assert.ThrowsAny<Exception>(() => BrotliCodec.Decompress(garbage));
        }
    }
}
