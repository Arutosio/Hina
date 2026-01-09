using System;
using System.Collections.Generic;

namespace Hina.Core.Manifest
{
    // Per-file entry with rsync-like chunk map.
    public sealed class ManifestFile
    {
        public string Path { get; set; } = string.Empty;
        public long Size { get; set; }
        public DateTimeOffset MTimeUtc { get; set; }
        public string FileHash { get; set; } = string.Empty;
        public int ChunkSize { get; set; }
        public List<ManifestChunk> Chunks { get; set; } = new List<ManifestChunk>();
    }
}
