using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Manifest;

namespace Hina.Core.Rsync
{
    // Abstraction for chunking strategies (fixed-size or content-defined).
    public interface IChunker
    {
        Task<List<ManifestChunk>> ChunkAsync(Stream stream, CancellationToken ct);
    }
}
