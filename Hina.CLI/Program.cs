using System;
using System.IO;
using System.Threading;
using Hina.Core.Configuration;
using Hina.Core.Patching;
using Microsoft.Extensions.Logging;

namespace Hina.CLI
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length == 0 || HasArg(args, "help") || HasArg(args, "--help"))
            {
                PrintHelp();
                return 0;
            }

            string command = args[0].ToLowerInvariant();
            string? root = GetArgValue(args, "--dir");
            string? baseUrl = GetArgValue(args, "--base");
            string? configPath = GetArgValue(args, "--config");
            string? trustedKey = GetArgValue(args, "--pubkey");
            bool verbose = HasArg(args, "--verbose") || HasArg(args, "-v");

            using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
            });

            ILogger logger = loggerFactory.CreateLogger("Hina.CLI");

            if (string.IsNullOrWhiteSpace(root))
            {
                logger.LogError("Missing required --dir <path>");
                return 2;
            }

            PatcherConfig config = LoadConfigOrDefault(configPath);

            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                config = ApplyOverrides(config, new Uri(baseUrl), null, null);
            }

            if (string.IsNullOrWhiteSpace(config.BaseUrl?.ToString()))
            {
                logger.LogError("Missing required --base <url> or config baseUrl");
                return 2;
            }

            if (!string.IsNullOrWhiteSpace(trustedKey))
            {
                config = ApplyOverrides(config, null, trustedKey, null);
            }

            string? channel = GetArgValue(args, "--channel");
            if (!string.IsNullOrWhiteSpace(channel))
            {
                config = ApplyOverrides(config, null, null, channel);
            }

            ILogger<PatchClient> clientLogger = loggerFactory.CreateLogger<PatchClient>();
            PatchClient client = new PatchClient(config, clientLogger);
            CancellationToken ct = CancellationToken.None;

            switch (command)
            {
                case "check":
                    {
                        var res = client.CheckAsync(root, ct).Result;
                        logger.LogInformation("{Message}", res.Message);
                        return res.IsUpdateAvailable ? 1 : 0;
                    }
                case "patch":
                    {
                        var res = client.PatchAsync(root, ct).Result;
                        logger.LogInformation("{Message}", res.Message);
                        return res.Success ? 0 : 2;
                    }
                case "verify":
                    {
                        var res = client.VerifyAsync(root, ct).Result;
                        logger.LogInformation("{Message}", res.Message);
                        return res.Success ? 0 : 3;
                    }
                case "rollback":
                    client.RollbackAsync(root, ct).Wait();
                    logger.LogInformation("Rollback complete");
                    return 0;
                case "cleanup":
                    PatchCleanup.Cleanup(root);
                    logger.LogInformation("Cleanup complete");
                    return 0;
                default:
                    logger.LogError("Unknown command: {Command}", command);
                    PrintHelp();
                    return 2;
            }

        }

        private static void PrintHelp()
        {
            Console.WriteLine("Hina CLI");
            Console.WriteLine("Usage:");
            Console.WriteLine("  hina <command> --dir <path> --base <url> [--channel stable] [-v|--verbose]");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("  check   - Check for updates");
            Console.WriteLine("  patch   - Apply updates");
            Console.WriteLine("  verify  - Verify local files");
            Console.WriteLine("  rollback- Rollback last patch");
            Console.WriteLine("  cleanup - Remove leftover temp/backup files");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --config <file>  - JSON config file");
            Console.WriteLine("  --pubkey <b64>   - Trusted public key for manifest verification");
            Console.WriteLine("  -v, --verbose    - Enable verbose/debug logging");
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

        private static PatcherConfig LoadConfigOrDefault(string? configPath)
        {
            if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
            {
                return PatcherConfigLoader.Load(configPath);
            }

            if (File.Exists("hina.config.json"))
            {
                return PatcherConfigLoader.Load("hina.config.json");
            }

            return new PatcherConfig();
        }

        private static PatcherConfig ApplyOverrides(PatcherConfig current, Uri? baseUrl, string? trustedKey, string? channel)
        {
            return new PatcherConfig
            {
                BaseUrl = baseUrl ?? current.BaseUrl,
                Channel = channel ?? current.Channel,
                Concurrency = current.Concurrency,
                ChunkSize = current.ChunkSize,
                Verify = current.Verify,
                Backup = current.Backup,
                TrustedPublicKey = trustedKey ?? current.TrustedPublicKey
            };
        }
    }
}
