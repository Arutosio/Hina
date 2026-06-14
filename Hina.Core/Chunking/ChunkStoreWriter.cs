using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Compression;
using Hina.Core.Hashing;
using Hina.Core.Inputs;
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
            await WriteChunksAsync(InputSet.Resolve(new[] { root }), chunkStoreDir, ct);
        }

        public async Task WriteChunksAsync(InputSet inputs, DirectoryInfo chunkStoreDir, CancellationToken ct)
        {
            chunkStoreDir.Create();

            if (_chunker != null)
            {
                await WriteChunksWithChunkerAsync(inputs.Files, chunkStoreDir, ct);
            }
            else
            {
                await WriteChunksFixedAsync(inputs.Files, chunkStoreDir, ct);
            }
        }

        private async Task WriteChunksWithChunkerAsync(IReadOnlyList<InputFile> files, DirectoryInfo chunkStoreDir, CancellationToken ct)
        {
            foreach (InputFile file in files)
            {
                ct.ThrowIfCancellationRequested();

                byte[] fileData = await File.ReadAllBytesAsync(file.AbsolutePath, ct);
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
                        await WriteAtomicAsync(outPath, compressed, ct);
                    }

                    offset += chunk.Size;
                }
            }
        }

        private async Task WriteChunksFixedAsync(IReadOnlyList<InputFile> files, DirectoryInfo chunkStoreDir, CancellationToken ct)
        {
            // Store chunks by strong hash so clients can fetch missing blocks.
            foreach (InputFile file in files)
            {
                ct.ThrowIfCancellationRequested();

                using (FileStream fs = File.OpenRead(file.AbsolutePath))
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
                                byte[] compressed = BrotliCodec.Compress(buffer.AsSpan(0, read).ToArray());
                                await WriteAtomicAsync(outPath, compressed, ct);
                            }
                        }

                        index++;
                        fileOffset += read;
                    }
                }
            }
        }

        // Writes 'data' atomically to 'canonicalPath' by first writing to a sibling
        // temp file and then renaming. If the write is interrupted (cancellation or
        // crash) the temp file is cleaned up and the canonical path is never touched,
        // so a partial/corrupt chunk can never be mistaken for a complete one.
        private static async Task WriteAtomicAsync(string canonicalPath, byte[] data, CancellationToken ct)
        {
            string dir = Path.GetDirectoryName(canonicalPath)!;
            string tempPath = Path.Combine(dir, Path.GetFileName(canonicalPath) + ".tmp" + Guid.NewGuid().ToString("N"));
            try
            {
                using (FileStream tmp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           bufferSize: 65536, useAsync: true))
                {
                    await tmp.WriteAsync(data.AsMemory(0, data.Length), ct);
                    await tmp.FlushAsync(ct);
                }
                // Atomic promotion: if the canonical path was created by a concurrent
                // writer between our File.Exists check and here, overwriting is safe
                // because content-addressed chunks with the same hash are byte-identical.
                File.Move(tempPath, canonicalPath, overwrite: true);
            }
            catch
            {
                try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
                throw;
            }
        }
    }
}
