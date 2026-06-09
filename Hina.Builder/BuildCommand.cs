using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Chunking;
using Hina.Core.Hashing;
using Hina.Core.Manifest;
using Hina.Core.Rsync;
using Microsoft.Extensions.Logging;

namespace Hina.Builder
{
    // `hina-builder build` — scans a directory, chunks every file, writes manifest + chunk store.
    // Extracted from Program.Main so the init wizard can drive the same build path after it
    // generates the descriptor.
    internal sealed class BuildOptions
    {
        public string Input { get; init; } = string.Empty;
        public string Output { get; init; } = string.Empty;
        public string BaseUrl { get; init; } = string.Empty;
        public string? SignKeyPath { get; init; }
        public string Version { get; init; } = "0.0.0";
        public int ChunkSize { get; init; } = 64 * 1024;
        public string ChunkingMode { get; init; } = "fixed";
        public int MinChunk { get; init; } = 2048;
        public int MaxChunk { get; init; } = 64 * 1024;
        public int AvgChunk { get; init; } = 8192;

        public static BuildOptions FromArgs(string[] args) => new BuildOptions
        {
            Input = Args.GetArgValue(args, "--input") ?? string.Empty,
            Output = Args.GetArgValue(args, "--out") ?? string.Empty,
            BaseUrl = Args.GetArgValue(args, "--base") ?? string.Empty,
            SignKeyPath = Args.GetArgValue(args, "--sign-key"),
            Version = Args.GetArgValue(args, "--version") ?? "0.0.0",
            ChunkSize = Args.ParseInt(Args.GetArgValue(args, "--chunk"), 64 * 1024),
            ChunkingMode = Args.GetArgValue(args, "--chunking") ?? "fixed",
            MinChunk = Args.ParseInt(Args.GetArgValue(args, "--min-chunk"), 2048),
            MaxChunk = Args.ParseInt(Args.GetArgValue(args, "--max-chunk"), 64 * 1024),
            AvgChunk = Args.ParseInt(Args.GetArgValue(args, "--avg-chunk"), 8192),
        };
    }

    internal static class BuildCommand
    {
        public static async Task<int> RunAsync(BuildOptions options, ILogger logger, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(options.Input) || string.IsNullOrWhiteSpace(options.Output) || string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                logger.LogError("Missing required args: --input, --out, --base");
                return 2;
            }

            DirectoryInfo inputDir = new DirectoryInfo(options.Input);
            DirectoryInfo outputDir = new DirectoryInfo(options.Output);
            DirectoryInfo chunkDir = new DirectoryInfo(Path.Combine(outputDir.FullName, "chunks"));

            IHasher hasher = new Sha256Hasher();
            ManifestBuilder manifestBuilder = new ManifestBuilder(hasher);

            IChunker chunker;
            if (string.Equals(options.ChunkingMode, "cdc", StringComparison.OrdinalIgnoreCase))
            {
                chunker = new ContentDefinedChunker(hasher, options.MinChunk, options.MaxChunk, options.AvgChunk);
                logger.LogInformation("Using content-defined chunking (min={Min}, max={Max}, avg={Avg})", options.MinChunk, options.MaxChunk, options.AvgChunk);
            }
            else
            {
                chunker = new RsyncChunker(options.ChunkSize, hasher);
                logger.LogInformation("Using fixed-size chunking (size={Size})", options.ChunkSize);
            }

            ChunkStoreWriter chunkWriter = new ChunkStoreWriter(hasher, options.ChunkSize, chunker);

            string normalizedBase = NormalizeBaseUrl(options.BaseUrl);
            logger.LogInformation("Building manifest from {InputDir}", inputDir.FullName);
            Manifest manifest = await manifestBuilder.BuildAsync(inputDir, new Uri(normalizedBase), options.ChunkSize, chunker, ct);
            manifest.Version = options.Version;
            if (!string.IsNullOrWhiteSpace(options.SignKeyPath))
            {
                byte[] privateKey = Convert.FromBase64String(File.ReadAllText(options.SignKeyPath).Trim());
                ManifestSigner.AttachSignature(manifest, privateKey);
                logger.LogInformation("Manifest signed with key from {KeyPath}", options.SignKeyPath);
            }

            outputDir.Create();
            await ManifestSerializer.WriteAsync(manifest, Path.Combine(outputDir.FullName, "manifest.json"), ct);
            await chunkWriter.WriteChunksAsync(inputDir, chunkDir, ct);

            logger.LogInformation("Build complete");
            logger.LogInformation("Manifest: {ManifestPath}", Path.Combine(outputDir.FullName, "manifest.json"));
            logger.LogInformation("Chunks: {ChunkDir}", chunkDir.FullName);
            return 0;
        }

        private static string NormalizeBaseUrl(string url)
        {
            // Ensure base URL is safe for Uri combination.
            return url.EndsWith("/") ? url : url + "/";
        }
    }
}
