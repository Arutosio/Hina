using System;

namespace Hina.Core.Rsync
{
    // Rsync rolling checksum (weak) for chunk matching.
    public static class RollingChecksum
    {
        private const uint Mod = 65521;

        public static uint Compute(ReadOnlySpan<byte> buffer)
        {
            uint a = 0;
            uint b = 0;
            for (int i = 0; i < buffer.Length; i++)
            {
                a = (a + buffer[i]) % Mod;
                b = (b + a) % Mod;
            }
            return (b << 16) | a;
        }

        public static uint Roll(uint prev, byte remove, byte add, int blockSize)
        {
            uint a = prev & 0xFFFF;
            uint b = (prev >> 16) & 0xFFFF;

            a = (a + Mod - remove + add) % Mod;
            // blockSize * remove must be computed in 64-bit: blockSize is the chunk size and
            // `remove` can be up to 255, so the product overflows int32 once chunkSize exceeds
            // ~8.4 MB, corrupting the weak checksum (and silently disabling rsync matching).
            b = (b + Mod - (uint)((long)blockSize * remove % Mod) + a) % Mod;

            return (b << 16) | a;
        }
    }
}
