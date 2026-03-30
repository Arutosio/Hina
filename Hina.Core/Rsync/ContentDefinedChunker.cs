using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Hashing;
using Hina.Core.Manifest;

namespace Hina.Core.Rsync
{
    // Content-defined chunking using a Gear hash to find chunk boundaries.
    // Produces variable-size chunks whose boundaries are determined by file content,
    // giving better deduplication when data is inserted or removed.
    public sealed class ContentDefinedChunker : IChunker
    {
        private readonly int _minSize;
        private readonly int _maxSize;
        private readonly int _avgSize;
        private readonly IHasher _hasher;

        // Pre-computed random table for the Gear hash (256 entries).
        private static readonly ulong[] GearTable = GenerateGearTable();

        public ContentDefinedChunker(IHasher hasher, int minSize = 2048, int maxSize = 65536, int avgSize = 8192)
        {
            if (minSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(minSize));
            if (maxSize <= 0 || maxSize < minSize)
                throw new ArgumentOutOfRangeException(nameof(maxSize));
            if (avgSize <= 0 || avgSize < minSize || avgSize > maxSize)
                throw new ArgumentOutOfRangeException(nameof(avgSize));

            _minSize = minSize;
            _maxSize = maxSize;
            _avgSize = avgSize;
            _hasher = hasher;
        }

        public async Task<List<ManifestChunk>> ChunkAsync(Stream stream, CancellationToken ct)
        {
            List<ManifestChunk> chunks = new List<ManifestChunk>();

            // Mask controls average chunk size: mask has log2(avgSize) bits set.
            ulong mask = ComputeMask(_avgSize);

            // Read entire stream into memory for simplicity (same pattern as fixed chunker).
            // For very large files a streaming approach could be used instead.
            byte[] data;
            if (stream is MemoryStream ms && ms.TryGetBuffer(out ArraySegment<byte> seg))
            {
                data = new byte[seg.Count];
                Buffer.BlockCopy(seg.Array!, seg.Offset, data, 0, seg.Count);
            }
            else
            {
                using (var temp = new MemoryStream())
                {
                    await stream.CopyToAsync(temp, 81920, ct);
                    data = temp.ToArray();
                }
            }

            if (data.Length == 0)
                return chunks;

            int offset = 0;
            int index = 0;

            while (offset < data.Length)
            {
                ct.ThrowIfCancellationRequested();

                int remaining = data.Length - offset;
                int chunkLen;

                if (remaining <= _minSize)
                {
                    // Not enough data left for a full min-size chunk; emit remainder.
                    chunkLen = remaining;
                }
                else
                {
                    int scanLimit = Math.Min(remaining, _maxSize);
                    chunkLen = scanLimit; // default: hit max without finding boundary

                    ulong hash = 0;
                    // Start scanning after minSize bytes.
                    for (int i = 0; i < scanLimit; i++)
                    {
                        hash = (hash << 1) + GearTable[data[offset + i]];

                        if (i >= _minSize - 1 && (hash & mask) == 0)
                        {
                            chunkLen = i + 1;
                            break;
                        }
                    }
                }

                // Compute weak (rolling) and strong hashes for this chunk.
                ReadOnlySpan<byte> chunkSpan = data.AsSpan(offset, chunkLen);
                uint weak = RollingChecksum.Compute(chunkSpan);

                string strong;
                using (var chunkStream = new MemoryStream(data, offset, chunkLen, writable: false, publiclyVisible: true))
                {
                    strong = await _hasher.ComputeHashAsync(chunkStream, ct);
                }

                chunks.Add(new ManifestChunk
                {
                    Index = index,
                    Weak = weak,
                    Strong = strong,
                    Size = chunkLen
                });

                offset += chunkLen;
                index++;
            }

            return chunks;
        }

        // Compute a bitmask that yields ~avgSize average chunk size.
        // We need approximately log2(avgSize) low bits set.
        private static ulong ComputeMask(int avgSize)
        {
            int bits = 0;
            int v = avgSize;
            while (v > 1)
            {
                v >>= 1;
                bits++;
            }
            return (1UL << bits) - 1;
        }

        // Deterministic pseudo-random table for Gear hash.
        private static ulong[] GenerateGearTable()
        {
            ulong[] table = new ulong[256];
            // Use a simple deterministic PRNG seeded with a fixed value.
            ulong state = 0x123456789ABCDEF0UL;
            for (int i = 0; i < 256; i++)
            {
                // xorshift64
                state ^= state << 13;
                state ^= state >> 7;
                state ^= state << 17;
                table[i] = state;
            }
            return table;
        }
    }
}
