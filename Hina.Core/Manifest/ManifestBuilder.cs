using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Hashing;
using Hina.Core.Rsync;

namespace Hina.Core.Manifest
{
    // Creates a manifest from a directory.
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
            if (!root.Exists)
            {
                throw new DirectoryNotFoundException(root.FullName);
            }

            Manifest manifest = new Manifest
            {
                BaseUrl = baseUrl.ToString()
            };

            List<FileInfo> files = new List<FileInfo>();
            foreach (string filePath in Directory.EnumerateFiles(root.FullName, "*", SearchOption.AllDirectories))
            {
                files.Add(new FileInfo(filePath));
            }

            foreach (FileInfo file in files)
            {
                ct.ThrowIfCancellationRequested();

                using (FileStream fs = file.OpenRead())
                {
                    List<ManifestChunk> chunks = await chunker.ChunkAsync(fs, ct);
                    fs.Position = 0;
                    string fileHash = await _hasher.ComputeHashAsync(fs, ct);

                    manifest.Files.Add(new ManifestFile
                    {
                        Path = Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/'),
                        Size = file.Length,
                        MTimeUtc = file.LastWriteTimeUtc,
                        FileHash = fileHash,
                        ChunkSize = chunkSize,
                        Chunks = chunks
                    });
                }
            }

            return manifest;
        }
    }
}
