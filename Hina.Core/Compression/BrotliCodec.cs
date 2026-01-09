using System.IO;
using System.IO.Compression;

namespace Hina.Core.Compression
{
    // Simple Brotli compression helper for chunk storage.
    public static class BrotliCodec
    {
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

        public static byte[] Decompress(byte[] data)
        {
            using (var input = new MemoryStream(data))
            using (var bs = new BrotliStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                bs.CopyTo(output);
                return output.ToArray();
            }
        }
    }
}
