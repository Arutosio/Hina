using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Hashing;
using Hina.Core.Manifest;

namespace Hina.Core.Rsync
{
    // Builds fixed-size chunks with weak+strong checksums.
    public sealed class RsyncChunker : IChunker
    {
        private readonly int _chunkSize;
        private readonly IHasher _hasher;

        public RsyncChunker(int chunkSize, IHasher hasher)
        {
            if (chunkSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(chunkSize));
            }

            _chunkSize = chunkSize;
            _hasher = hasher;
        }

        public Task<List<ManifestChunk>> ChunkAsync(Stream stream, CancellationToken ct)
            => BuildChunksAsync(stream, ct);

        public async Task<List<ManifestChunk>> BuildChunksAsync(Stream stream, CancellationToken ct)
        {
            List<ManifestChunk> chunks = new List<ManifestChunk>();
            byte[] buffer = new byte[_chunkSize];
            int index = 0;
            int read;

            // Read the stream in fixed-size blocks and emit chunk hashes.
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                uint weak = RollingChecksum.Compute(buffer.AsSpan(0, read));

                // Compute strong hash on the exact chunk bytes.
                using (var ms = new MemoryStream(buffer, 0, read, writable: false, publiclyVisible: true))
                {
                    string strong = await _hasher.ComputeHashAsync(ms, ct);
                    chunks.Add(new ManifestChunk
                    {
                        Index = index,
                        Weak = weak,
                        Strong = strong,
                        Size = read
                    });
                }

                index++;
            }

            return chunks;
        }
    }
}
