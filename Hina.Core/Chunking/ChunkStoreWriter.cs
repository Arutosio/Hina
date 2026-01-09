using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Compression;
using Hina.Core.Hashing;

namespace Hina.Core.Chunking
{
    // Writes raw chunk files to a chunk store on disk.
    public sealed class ChunkStoreWriter
    {
        private readonly IHasher _hasher;
        private readonly int _chunkSize;

        public ChunkStoreWriter(IHasher hasher, int chunkSize)
        {
            _hasher = hasher;
            _chunkSize = chunkSize;
        }

        public async Task WriteChunksAsync(DirectoryInfo root, DirectoryInfo chunkStoreDir, CancellationToken ct)
        {
            if (!root.Exists)
            {
                throw new DirectoryNotFoundException(root.FullName);
            }

            chunkStoreDir.Create();

            // Store chunks by strong hash so clients can fetch missing blocks.
            foreach (string filePath in Directory.EnumerateFiles(root.FullName, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                using (FileStream fs = File.OpenRead(filePath))
                {
                    long fileOffset = 0;
                    int index = 0;
                    byte[] buffer = new byte[_chunkSize];
                    int read;

                    while ((read = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                    {
                        using (var ms = new MemoryStream(buffer, 0, read, writable: false, publiclyVisible: true))
                        {
                            string strong = await _hasher.ComputeHashAsync(ms, ct);
                            string hashOnly = strong.Replace(_hasher.AlgorithmId + ":", "");

                            // Bucket by hash prefix to avoid huge directories.
                            string bucket = hashOnly.Substring(0, 2);
                            string outDir = Path.Combine(chunkStoreDir.FullName, bucket);
                            Directory.CreateDirectory(outDir);

                            string outPath = Path.Combine(outDir, hashOnly + ".chunk.br");
                            if (!File.Exists(outPath))
                            {
                                using (FileStream outFs = File.Create(outPath))
                                {
                                    byte[] compressed = BrotliCodec.Compress(buffer.AsSpan(0, read).ToArray());
                                    await outFs.WriteAsync(compressed.AsMemory(0, compressed.Length), ct);
                                }
                            }
                        }

                        index++;
                        fileOffset += read;
                    }
                }
            }
        }
    }
}
