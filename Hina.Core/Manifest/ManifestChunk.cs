namespace Hina.Core.Manifest
{
    // Weak is rsync rolling checksum, strong is cryptographic hash.
    public sealed class ManifestChunk
    {
        public int Index { get; set; }
        public uint Weak { get; set; }
        public string Strong { get; set; } = string.Empty;
        public int Size { get; set; }
    }
}
