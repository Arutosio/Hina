using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Cli;
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

        // Variant token (e.g. "windows-x64", "linux") for a multi-platform app. When set, the
        // manifest is written as manifest.<token>.json into the same --out (shared chunk store),
        // so each variant's build dedupes common chunks against the others.
        public string? Platform { get; init; }

        public static BuildOptions FromArgs(string[] args) => new BuildOptions
        {
            Platform = Args.GetValue(args, "--platform"),
            Input = Args.GetValue(args, "--input") ?? string.Empty,
            Output = Args.GetValue(args, "--out") ?? string.Empty,
            BaseUrl = Args.GetValue(args, "--base") ?? string.Empty,
            SignKeyPath = Args.GetValue(args, "--sign-key"),
            Version = Args.GetValue(args, "--version") ?? "0.0.0",
            ChunkSize = Args.ParseInt(Args.GetValue(args, "--chunk"), 64 * 1024),
            ChunkingMode = Args.GetValue(args, "--chunking") ?? "fixed",
            MinChunk = Args.ParseInt(Args.GetValue(args, "--min-chunk"), 2048),
            MaxChunk = Args.ParseInt(Args.GetValue(args, "--max-chunk"), 64 * 1024),
            AvgChunk = Args.ParseInt(Args.GetValue(args, "--avg-chunk"), 8192),
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

            string manifestName = "manifest.json";
            if (!string.IsNullOrEmpty(options.Platform))
            {
                if (!IsValidPlatformToken(options.Platform))
                {
                    logger.LogError("Invalid --platform '{Token}'. Expected <os>[-<arch>], os ∈ windows|macos|linux, arch ∈ x64|arm64|x86|arm.", options.Platform);
                    return 2;
                }
                manifestName = $"manifest.{options.Platform}.json";
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
            await ManifestSerializer.WriteAsync(manifest, Path.Combine(outputDir.FullName, manifestName), ct);
            await chunkWriter.WriteChunksAsync(inputDir, chunkDir, ct);

            logger.LogInformation("Build complete");
            logger.LogInformation("Manifest: {ManifestPath}", Path.Combine(outputDir.FullName, manifestName));
            logger.LogInformation("Chunks: {ChunkDir}", chunkDir.FullName);
            return 0;
        }

        private static string NormalizeBaseUrl(string url)
        {
            // Ensure base URL is safe for Uri combination.
            return url.EndsWith("/") ? url : url + "/";
        }

        // <os>[-<arch>]; os ∈ {windows,macos,linux}, arch ∈ {x64,arm64,x86,arm}. Shares the closed
        // sets with DescriptorValidator so the build token and the descriptor's platforms[] agree.
        private static bool IsValidPlatformToken(string token)
        {
            int dash = token.IndexOf('-');
            string os = dash < 0 ? token : token.Substring(0, dash);
            string? arch = dash < 0 ? null : token.Substring(dash + 1);
            if (!Hina.PackageManager.Descriptor.DescriptorValidator.KnownOs.Contains(os))
            {
                return false;
            }
            return arch == null || Hina.PackageManager.Descriptor.DescriptorValidator.KnownArch.Contains(arch);
        }
    }
}
