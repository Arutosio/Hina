using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Chunking;
using Hina.Core.Hashing;
using Hina.Core.Manifest;

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

            string command = args[0].ToLowerInvariant();
            if (command == "keygen")
            {
                return RunKeygen(args);
            }

            if (command != "build")
            {
                Console.WriteLine("Unknown command.");
                PrintHelp();
                return 2;
            }

            string? input = GetArgValue(args, "--input");
            string? output = GetArgValue(args, "--out");
            string? baseUrl = GetArgValue(args, "--base");
            string? signKeyPath = GetArgValue(args, "--sign-key");
            string version = GetArgValue(args, "--version") ?? "0.0.0";
            int chunkSize = ParseInt(GetArgValue(args, "--chunk"), 64 * 1024);

            if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(baseUrl))
            {
                Console.WriteLine("Missing required args: --input, --out, --base.");
                return 2;
            }

            DirectoryInfo inputDir = new DirectoryInfo(input);
            DirectoryInfo outputDir = new DirectoryInfo(output);
            DirectoryInfo chunkDir = new DirectoryInfo(Path.Combine(outputDir.FullName, "chunks"));

            IHasher hasher = new Sha256Hasher();
            ManifestBuilder builder = new ManifestBuilder(hasher);
            ChunkStoreWriter chunkWriter = new ChunkStoreWriter(hasher, chunkSize);

            string normalizedBase = NormalizeBaseUrl(baseUrl);
            Manifest manifest = await builder.BuildAsync(inputDir, new Uri(normalizedBase), chunkSize, CancellationToken.None);
            manifest.Version = version;
            if (!string.IsNullOrWhiteSpace(signKeyPath))
            {
                byte[] privateKey = Convert.FromBase64String(File.ReadAllText(signKeyPath).Trim());
                ManifestSigner.AttachSignature(manifest, privateKey);
            }

            outputDir.Create();
            await ManifestSerializer.WriteAsync(manifest, Path.Combine(outputDir.FullName, "manifest.json"), CancellationToken.None);
            await chunkWriter.WriteChunksAsync(inputDir, chunkDir, CancellationToken.None);

            Console.WriteLine("Build complete.");
            Console.WriteLine($"Manifest: {Path.Combine(outputDir.FullName, "manifest.json")}");
            Console.WriteLine($"Chunks: {chunkDir.FullName}");
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
            Console.WriteLine("  hina-builder build --input <dir> --out <dir> --base <url> [--version v] [--chunk 65536] [--sign-key key.b64]");
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

        private static int RunKeygen(string[] args)
        {
            string? outDir = GetArgValue(args, "--out") ?? ".";
            string name = GetArgValue(args, "--name") ?? "hina";

            Directory.CreateDirectory(outDir);
            var pair = Hina.Core.Crypto.KeyGenerator.GenerateEd25519();

            string privPath = Path.Combine(outDir, $"{name}.key.b64");
            string pubPath = Path.Combine(outDir, $"{name}.pub.b64");

            File.WriteAllText(privPath, pair.PrivateKeyBase64);
            File.WriteAllText(pubPath, pair.PublicKeyBase64);

            Console.WriteLine("Key pair generated:");
            Console.WriteLine(privPath);
            Console.WriteLine(pubPath);
            return 0;
        }

        private static string NormalizeBaseUrl(string url)
        {
            // Ensure base URL is safe for Uri combination.
            return url.EndsWith("/") ? url : url + "/";
        }
    }
}
