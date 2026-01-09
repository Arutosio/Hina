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
            b = (b + Mod - (uint)(blockSize * remove % Mod) + a) % Mod;

            return (b << 16) | a;
        }
    }
}
