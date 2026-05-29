using System.IO;
using System.IO.Compression;

namespace Hina.Core.Compression
{
    // Simple Brotli compression helper for chunk storage.
    public static class BrotliCodec
    {
        // Absolute backstop when no tighter bound is supplied. A single chunk is normally KB-MB;
        // this only exists to stop a decompression bomb from a hostile chunk store, not to bound
        // legitimate data.
        public const long DefaultMaxDecompressedBytes = 256L * 1024 * 1024;

        public static byte[] Compress(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                using (var bs = new BrotliStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                {
                    bs.Write(data, 0, data.Length);
                }
                return ms.ToArray();
            }
        }

        // Decompress, refusing to buffer more than maxBytes. Network-sourced Brotli can expand
        // by orders of magnitude; without a cap a tiny ".chunk.br" could OOM the client before
        // any hash check runs. Throws InvalidDataException once the limit is exceeded.
        public static byte[] Decompress(byte[] data, long maxBytes = DefaultMaxDecompressedBytes)
        {
            using (var input = new MemoryStream(data))
            using (var bs = new BrotliStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                byte[] buffer = new byte[81920];
                long total = 0;
                int n;
                while ((n = bs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += n;
                    if (total > maxBytes)
                    {
                        throw new InvalidDataException(
                            $"Decompressed data exceeds the {maxBytes}-byte limit (possible decompression bomb).");
                    }
                    output.Write(buffer, 0, n);
                }
                return output.ToArray();
            }
        }
    }
}
