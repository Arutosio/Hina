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
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            if (args.Length == 0 || HasArg(args, "--help") || HasArg(args, "help"))
            {
                PrintHelp();
                return 0;
            }

            bool verbose = HasArg(args, "--verbose") || HasArg(args, "-v");

            using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
            });

            ILogger logger = loggerFactory.CreateLogger("Hina.Builder");

            string command = args[0].ToLowerInvariant();
            if (command == "keygen")
            {
                return RunKeygen(args, logger);
            }

            if (command != "build")
            {
                logger.LogError("Unknown command: {Command}", command);
                PrintHelp();
                return 2;
            }

            string? input = GetArgValue(args, "--input");
            string? output = GetArgValue(args, "--out");
            string? baseUrl = GetArgValue(args, "--base");
            string? signKeyPath = GetArgValue(args, "--sign-key");
            string version = GetArgValue(args, "--version") ?? "0.0.0";
            int chunkSize = ParseInt(GetArgValue(args, "--chunk"), 64 * 1024);
            string chunkingMode = GetArgValue(args, "--chunking") ?? "fixed";
            int minChunk = ParseInt(GetArgValue(args, "--min-chunk"), 2048);
            int maxChunk = ParseInt(GetArgValue(args, "--max-chunk"), 64 * 1024);
            int avgChunk = ParseInt(GetArgValue(args, "--avg-chunk"), 8192);

            if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(baseUrl))
            {
                logger.LogError("Missing required args: --input, --out, --base");
                return 2;
            }

            DirectoryInfo inputDir = new DirectoryInfo(input);
            DirectoryInfo outputDir = new DirectoryInfo(output);
            DirectoryInfo chunkDir = new DirectoryInfo(Path.Combine(outputDir.FullName, "chunks"));

            IHasher hasher = new Sha256Hasher();
            ManifestBuilder builder2 = new ManifestBuilder(hasher);

            IChunker chunker;
            if (string.Equals(chunkingMode, "cdc", StringComparison.OrdinalIgnoreCase))
            {
                chunker = new ContentDefinedChunker(hasher, minChunk, maxChunk, avgChunk);
                logger.LogInformation("Using content-defined chunking (min={Min}, max={Max}, avg={Avg})", minChunk, maxChunk, avgChunk);
            }
            else
            {
                chunker = new RsyncChunker(chunkSize, hasher);
                logger.LogInformation("Using fixed-size chunking (size={Size})", chunkSize);
            }

            ChunkStoreWriter chunkWriter = new ChunkStoreWriter(hasher, chunkSize, chunker);

            string normalizedBase = NormalizeBaseUrl(baseUrl);
            logger.LogInformation("Building manifest from {InputDir}", inputDir.FullName);
            Manifest manifest = await builder2.BuildAsync(inputDir, new Uri(normalizedBase), chunkSize, chunker, CancellationToken.None);
            manifest.Version = version;
            if (!string.IsNullOrWhiteSpace(signKeyPath))
            {
                byte[] privateKey = Convert.FromBase64String(File.ReadAllText(signKeyPath).Trim());
                ManifestSigner.AttachSignature(manifest, privateKey);
                logger.LogInformation("Manifest signed with key from {KeyPath}", signKeyPath);
            }

            outputDir.Create();
            await ManifestSerializer.WriteAsync(manifest, Path.Combine(outputDir.FullName, "manifest.json"), CancellationToken.None);
            await chunkWriter.WriteChunksAsync(inputDir, chunkDir, CancellationToken.None);

            logger.LogInformation("Build complete");
            logger.LogInformation("Manifest: {ManifestPath}", Path.Combine(outputDir.FullName, "manifest.json"));
            logger.LogInformation("Chunks: {ChunkDir}", chunkDir.FullName);
            return 0;
        }

        private static int ParseInt(string? value, int fallback)
        {
            return int.TryParse(value, out int v) ? v : fallback;
        }

        private static void PrintHelp()
        {
            Console.WriteLine("Hina Builder");
            Console.WriteLine("Usage:");
            Console.WriteLine("  hina-builder build --input <dir> --out <dir> --base <url> [--version v] [--chunk 65536] [--chunking fixed|cdc] [--min-chunk 2048] [--max-chunk 65536] [--avg-chunk 8192] [--sign-key key.b64] [-v|--verbose]");
            Console.WriteLine("  hina-builder keygen [--out <dir>] [--name <prefix>]");
        }

        private static bool HasArg(string[] args, string name)
        {
            foreach (string arg in args)
            {
                if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static string? GetArgValue(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }
            return null;
        }

        private static int RunKeygen(string[] args, ILogger logger)
        {
            string? outDir = GetArgValue(args, "--out") ?? ".";
            string name = GetArgValue(args, "--name") ?? "hina";

            Directory.CreateDirectory(outDir);
            var pair = Hina.Core.Crypto.KeyGenerator.GenerateEd25519();

            string privPath = Path.Combine(outDir, $"{name}.key.b64");
            string pubPath = Path.Combine(outDir, $"{name}.pub.b64");

            File.WriteAllText(privPath, pair.PrivateKeyBase64);
            File.WriteAllText(pubPath, pair.PublicKeyBase64);

            logger.LogInformation("Key pair generated: {PrivateKeyPath}, {PublicKeyPath}", privPath, pubPath);
            return 0;
        }

        private static string NormalizeBaseUrl(string url)
        {
            // Ensure base URL is safe for Uri combination.
            return url.EndsWith("/") ? url : url + "/";
        }
    }
}
