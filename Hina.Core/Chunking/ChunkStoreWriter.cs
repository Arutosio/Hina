using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Compression;
using Hina.Core.Hashing;
using Hina.Core.Manifest;
using Hina.Core.Rsync;

namespace Hina.Core.Chunking
{
    // Writes raw chunk files to a chunk store on disk.
    public sealed class ChunkStoreWriter
    {
        private readonly IHasher _hasher;
        private readonly int _chunkSize;
        private readonly IChunker? _chunker;

        public ChunkStoreWriter(IHasher hasher, int chunkSize)
        {
            _hasher = hasher;
            _chunkSize = chunkSize;
        }

        public ChunkStoreWriter(IHasher hasher, int chunkSize, IChunker chunker)
        {
            _hasher = hasher;
            _chunkSize = chunkSize;
            _chunker = chunker;
        }

        public async Task WriteChunksAsync(DirectoryInfo root, DirectoryInfo chunkStoreDir, CancellationToken ct)
        {
            if (!root.Exists)
            {
                throw new DirectoryNotFoundException(root.FullName);
            }

            chunkStoreDir.Create();

            if (_chunker != null)
            {
                await WriteChunksWithChunkerAsync(root, chunkStoreDir, ct);
            }
            else
            {
                await WriteChunksFixedAsync(root, chunkStoreDir, ct);
            }
        }

        private async Task WriteChunksWithChunkerAsync(DirectoryInfo root, DirectoryInfo chunkStoreDir, CancellationToken ct)
        {
            foreach (string filePath in Directory.EnumerateFiles(root.FullName, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                byte[] fileData = await File.ReadAllBytesAsync(filePath, ct);
                List<ManifestChunk> chunks;
                using (var ms = new MemoryStream(fileData))
                {
                    chunks = await _chunker!.ChunkAsync(ms, ct);
                }

                int offset = 0;
                foreach (ManifestChunk chunk in chunks)
                {
                    string hashOnly = chunk.Strong.Replace(_hasher.AlgorithmId + ":", "");
                    string bucket = hashOnly.Substring(0, 2);
                    string outDir = Path.Combine(chunkStoreDir.FullName, bucket);
                    Directory.CreateDirectory(outDir);

                    string outPath = Path.Combine(outDir, hashOnly + ".chunk.br");
                    if (!File.Exists(outPath))
                    {
                        byte[] chunkData = new byte[chunk.Size];
                        Buffer.BlockCopy(fileData, offset, chunkData, 0, chunk.Size);
                        byte[] compressed = BrotliCodec.Compress(chunkData);
                        using (FileStream outFs = File.Create(outPath))
                        {
                            await outFs.WriteAsync(compressed.AsMemory(0, compressed.Length), ct);
                        }
                    }

                    offset += chunk.Size;
                }
            }
        }

        private async Task WriteChunksFixedAsync(DirectoryInfo root, DirectoryInfo chunkStoreDir, CancellationToken ct)
        {
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
