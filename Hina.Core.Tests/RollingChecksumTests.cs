using System;
using System.Text;
using Hina.Core.Rsync;
using Xunit;

namespace Hina.Core.Tests
{
    public class RollingChecksumTests
    {
        [Fact]
        public void Compute_NonZeroForNonEmptyInput()
        {
            byte[] data = Encoding.ASCII.GetBytes("hello");
            uint checksum = RollingChecksum.Compute(data);
            Assert.NotEqual(0u, checksum);
        }

        [Fact]
        public void Compute_ZeroForEmptyInput()
        {
            uint checksum = RollingChecksum.Compute(ReadOnlySpan<byte>.Empty);
            Assert.Equal(0u, checksum);
        }

        [Fact]
        public void Compute_DeterministicForSameInput()
        {
            byte[] data = Encoding.ASCII.GetBytes("abcdefgh");
            uint c1 = RollingChecksum.Compute(data);
            uint c2 = RollingChecksum.Compute(data);
            Assert.Equal(c1, c2);
        }

        [Fact]
        public void Compute_DifferentForDifferentInput()
        {
            uint c1 = RollingChecksum.Compute(Encoding.ASCII.GetBytes("aaaa"));
            uint c2 = RollingChecksum.Compute(Encoding.ASCII.GetBytes("bbbb"));
            Assert.NotEqual(c1, c2);
        }

        [Fact]
        public void Roll_MatchesRecompute_SingleStep()
        {
            byte[] data = Encoding.ASCII.GetBytes("abcdefghijklmnopqrstuvwxyz");
            int window = 8;

            uint w1 = RollingChecksum.Compute(data.AsSpan(0, window));
            uint rolled = RollingChecksum.Roll(w1, data[0], data[window], window);
            uint recomputed = RollingChecksum.Compute(data.AsSpan(1, window));

            Assert.Equal(recomputed, rolled);
        }

        [Fact]
        public void Roll_MatchesRecompute_MultipleSteps()
        {
            byte[] data = Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog!!");
            int window = 10;

            uint current = RollingChecksum.Compute(data.AsSpan(0, window));

            // Roll through several positions and verify each matches recompute
            for (int i = 0; i < data.Length - window; i++)
            {
                uint rolled = RollingChecksum.Roll(current, data[i], data[i + window], window);
                uint recomputed = RollingChecksum.Compute(data.AsSpan(i + 1, window));

                Assert.Equal(recomputed, rolled);
                current = rolled;
            }
        }

        [Fact]
        public void Roll_MatchesRecompute_SmallWindow()
        {
            byte[] data = new byte[] { 1, 2, 3, 4, 5, 6 };
            int window = 2;

            uint w1 = RollingChecksum.Compute(data.AsSpan(0, window));
            uint rolled = RollingChecksum.Roll(w1, data[0], data[window], window);
            uint recomputed = RollingChecksum.Compute(data.AsSpan(1, window));

            Assert.Equal(recomputed, rolled);
        }

        [Fact]
        public void Roll_LargeBlockSize_NoInt32Overflow()
        {
            // blockSize * remove (255) overflows int32 once blockSize > ~8.42 MB. Verify the
            // rolled checksum still matches a fresh recompute for such a window.
            int window = 8_500_000;
            byte[] data = new byte[window + 1];
            Array.Fill(data, (byte)0xFF, 0, window); // window is all 0xFF, so `remove` == 255
            data[window] = 0x00;                     // roll a different byte in

            uint w1 = RollingChecksum.Compute(data.AsSpan(0, window));
            uint rolled = RollingChecksum.Roll(w1, data[0], data[window], window);
            uint recomputed = RollingChecksum.Compute(data.AsSpan(1, window));

            Assert.Equal(recomputed, rolled);
        }

        [Fact]
        public void Compute_SingleByte()
        {
            byte[] data = new byte[] { 42 };
            uint checksum = RollingChecksum.Compute(data);
            // a = 42, b = 42 => (42 << 16) | 42
            Assert.Equal((42u << 16) | 42u, checksum);
        }
    }
}
