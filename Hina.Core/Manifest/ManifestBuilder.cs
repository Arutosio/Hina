using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Hashing;
using Hina.Core.Inputs;
using Hina.Core.Rsync;

namespace Hina.Core.Manifest
{
    // Creates a manifest from a directory (or a merged set of input roots).
    public sealed class ManifestBuilder
    {
        private readonly IHasher _hasher;

        public ManifestBuilder(IHasher hasher)
        {
            _hasher = hasher;
        }

        public async Task<Manifest> BuildAsync(
            DirectoryInfo root,
            Uri baseUrl,
            int chunkSize,
            CancellationToken ct)
        {
            IChunker chunker = new RsyncChunker(chunkSize, _hasher);
            return await BuildAsync(root, baseUrl, chunkSize, chunker, ct);
        }

        public async Task<Manifest> BuildAsync(
            DirectoryInfo root,
            Uri baseUrl,
            int chunkSize,
            IChunker chunker,
            CancellationToken ct)
        {
            return await BuildAsync(InputSet.Resolve(new[] { root }), baseUrl, chunkSize, chunker, ct);
        }

        public async Task<Manifest> BuildAsync(
            InputSet inputs,
            Uri baseUrl,
            int chunkSize,
            IChunker chunker,
            CancellationToken ct)
        {
            // Determine whether this is a fixed-size chunker (RsyncChunker) or a
            // variable-size chunker (e.g. ContentDefinedChunker).
            // For fixed-size runs ChunkSize carries the requested block size as a hint.
            // For variable-size CDC runs there is no single representative size, so we
            // record 0 to signal "variable" — the patch client derives window sizes from
            // the actual chunk.Size values recorded per chunk rather than from this field.
            bool isFixedSize = chunker is RsyncChunker;
            int descriptiveChunkSize = isFixedSize ? chunkSize : 0;

            Manifest manifest = new Manifest
            {
                BaseUrl = baseUrl.ToString()
            };

            foreach (InputFile file in inputs.Files)
            {
                ct.ThrowIfCancellationRequested();

                FileInfo info = new FileInfo(file.AbsolutePath);
                using (FileStream fs = info.OpenRead())
                {
                    List<ManifestChunk> chunks = await chunker.ChunkAsync(fs, ct);
                    fs.Position = 0;
                    string fileHash = await _hasher.ComputeHashAsync(fs, ct);

                    manifest.Files.Add(new ManifestFile
                    {
                        Path = file.RelativePath,
                        Size = info.Length,
                        MTimeUtc = info.LastWriteTimeUtc,
                        FileHash = fileHash,
                        ChunkSize = descriptiveChunkSize,
                        Chunks = chunks
                    });
                }
            }

            return manifest;
        }
    }
}
