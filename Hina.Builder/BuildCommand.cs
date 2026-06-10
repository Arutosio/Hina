using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Cli;
using Hina.Core.Chunking;
using Hina.Core.Hashing;
using Hina.Core.Inputs;
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

        // Optional shared-files folder merged into the build (game data identical across
        // variants). On a relative-path collision the --input copy wins, so a variant can
        // still override a shared asset.
        public string? Common { get; init; }

        public static BuildOptions FromArgs(string[] args) => new BuildOptions
        {
            Platform = Args.GetValue(args, "--platform"),
            Common = Args.GetValue(args, "--common"),
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

            // Chunk parameters are publisher input (--chunk / --min-chunk / --max-chunk /
            // --avg-chunk); a typo'd value must be a usage error, not an unhandled
            // ArgumentOutOfRangeException from the chunker constructors.
            if (options.ChunkSize <= 0)
            {
                logger.LogError("--chunk must be a positive number of bytes (got {Chunk}).", options.ChunkSize);
                return 2;
            }
            if (string.Equals(options.ChunkingMode, "cdc", StringComparison.OrdinalIgnoreCase) &&
                (options.MinChunk <= 0 || options.AvgChunk <= 0 || options.MaxChunk <= 0 ||
                 options.MinChunk > options.AvgChunk || options.AvgChunk > options.MaxChunk))
            {
                logger.LogError("CDC chunk sizes must be positive and ordered min <= avg <= max (got min={Min}, avg={Avg}, max={Max}).",
                    options.MinChunk, options.AvgChunk, options.MaxChunk);
                return 2;
            }

            // Same rule the init wizard enforces for its output: the chunk store and manifest
            // must live OUTSIDE the app folder. With --out inside --input, the next build
            // would chunk the previous build's store into the manifest, and clients would
            // download the whole chunk store as app content.
            if (IsInside(options.Output, options.Input))
            {
                logger.LogError("--out '{Out}' must be outside the --input folder '{Input}': the manifest and chunk store would be picked up as app files by the next build.",
                    options.Output, options.Input);
                return 2;
            }

            if (!string.IsNullOrWhiteSpace(options.Common))
            {
                if (!Directory.Exists(options.Common))
                {
                    logger.LogError("--common folder not found: {Common}", options.Common);
                    return 2;
                }
                if (IsInside(options.Common, options.Input) || IsInside(options.Input, options.Common))
                {
                    logger.LogError("--common '{Common}' must not be inside --input '{Input}' (or contain it): its files would be packaged twice.",
                        options.Common, options.Input);
                    return 2;
                }
                if (IsInside(options.Output, options.Common))
                {
                    logger.LogError("--out '{Out}' must be outside the --common folder '{Common}': the manifest and chunk store would be picked up as shared files by the next build.",
                        options.Output, options.Common);
                    return 2;
                }
                if (IsInside(options.Common, options.Output))
                {
                    logger.LogError("--common '{Common}' must be outside --out '{Out}'.", options.Common, options.Output);
                    return 2;
                }
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
            if (!Uri.TryCreate(normalizedBase, UriKind.Absolute, out Uri? baseUri))
            {
                logger.LogError("--base '{Url}' is not a valid absolute URL (expected e.g. https://cdn.example.com/mygame/).", options.BaseUrl);
                return 2;
            }
            // One resolved file set consumed by BOTH the manifest and the chunk store, so the
            // two can never disagree. Roots in precedence order: common first, --input last
            // (variant wins on a relative-path collision).
            List<DirectoryInfo> roots = new List<DirectoryInfo>();
            if (!string.IsNullOrWhiteSpace(options.Common))
            {
                roots.Add(new DirectoryInfo(options.Common));
            }
            roots.Add(inputDir);
            InputSet inputs = InputSet.Resolve(roots);

            if (roots.Count > 1)
            {
                int variantCount = inputs.Files.Count(f => f.SourceRoot == inputDir.FullName);
                logger.LogInformation("Merging shared files from {Common}: {CommonCount} common + {VariantCount} variant files ({OverrideCount} overridden by the variant).",
                    roots[0].FullName, inputs.Files.Count - variantCount, variantCount, inputs.Overrides.Count);
                foreach (InputOverride ov in inputs.Overrides)
                {
                    logger.LogInformation("Variant overrides common file: {Path}", ov.RelativePath);
                }
            }
            foreach (string clash in inputs.CaseClashes)
            {
                logger.LogWarning("Paths differing only by case will collide on Windows/macOS installs: {Path}", clash);
            }

            logger.LogInformation("Building manifest from {InputDir}", inputDir.FullName);
            Manifest manifest = await manifestBuilder.BuildAsync(inputs, baseUri, options.ChunkSize, chunker, ct);
            manifest.Version = options.Version;
            if (!string.IsNullOrWhiteSpace(options.SignKeyPath))
            {
                if (!File.Exists(options.SignKeyPath))
                {
                    logger.LogError("--sign-key file not found: {KeyPath}", options.SignKeyPath);
                    return 2;
                }
                byte[] privateKey;
                try
                {
                    privateKey = Convert.FromBase64String(File.ReadAllText(options.SignKeyPath).Trim());
                }
                catch (FormatException)
                {
                    logger.LogError("--sign-key '{KeyPath}' is not a base64 private key. Pass the ed25519.priv.b64 file generated by `hina-builder keygen`.", options.SignKeyPath);
                    return 2;
                }
                ManifestSigner.AttachSignature(manifest, privateKey);
                logger.LogInformation("Manifest signed with key from {KeyPath}", options.SignKeyPath);
            }

            outputDir.Create();
            await ManifestSerializer.WriteAsync(manifest, Path.Combine(outputDir.FullName, manifestName), ct);
            await chunkWriter.WriteChunksAsync(inputs, chunkDir, ct);

            logger.LogInformation("Build complete");
            logger.LogInformation("Manifest: {ManifestPath}", Path.Combine(outputDir.FullName, manifestName));
            logger.LogInformation("Chunks: {ChunkDir}", chunkDir.FullName);
            return 0;
        }

        // Mirrors InitCommand.IsInside: prefix match on normalized full paths, so equal
        // directories also count as "inside".
        private static bool IsInside(string child, string parent)
        {
            string c = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return c.StartsWith(p, StringComparison.Ordinal);
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
