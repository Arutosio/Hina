using System;
using System.IO;
using System.Threading;
using Hina.Core.Configuration;
using Hina.Core.Patching;
using Microsoft.Extensions.Logging;

namespace Hina.CLI.Commands
{
    // `hina dev <subcommand>` — original patcher operations, hidden from end-user help
    // but still available for app developers, CI, and troubleshooting.
    internal static class DevCommand
    {
        public static int Run(string[] args, ILoggerFactory loggerFactory, ILogger logger)
        {
            if (args.Length < 2)
            {
                PrintHelp();
                return 2;
            }

            string subcommand = args[1].ToLowerInvariant();
            string? root = Args.GetValue(args, "--dir");
            string? baseUrl = Args.GetValue(args, "--base");
            string? configPath = Args.GetValue(args, "--config");
            string? trustedKey = Args.GetValue(args, "--pubkey");
            string? channel = Args.GetValue(args, "--channel");

            if (subcommand == "help" || subcommand == "--help" || subcommand == "-h")
            {
                PrintHelp();
                return 0;
            }

            if (string.IsNullOrWhiteSpace(root))
            {
                logger.LogError("Missing required --dir <path>");
                return 2;
            }

            PatcherConfig config = LoadConfigOrDefault(configPath);
            if (!string.IsNullOrWhiteSpace(baseUrl)) config = ApplyOverrides(config, new Uri(baseUrl), null, null);
            if (!string.IsNullOrWhiteSpace(trustedKey)) config = ApplyOverrides(config, null, trustedKey, null);
            if (!string.IsNullOrWhiteSpace(channel)) config = ApplyOverrides(config, null, null, channel);

            if (string.IsNullOrWhiteSpace(config.BaseUrl?.ToString()))
            {
                logger.LogError("Missing required --base <url> or config baseUrl");
                return 2;
            }

            ILogger<PatchClient> clientLogger = loggerFactory.CreateLogger<PatchClient>();
            PatchClient client = new PatchClient(config, clientLogger);
            CancellationToken ct = CancellationToken.None;

            switch (subcommand)
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
                    logger.LogError("Unknown dev subcommand: {Sub}", subcommand);
                    PrintHelp();
                    return 2;
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("hina dev <subcommand> --dir <path> --base <url> [--channel stable] [--pubkey b64]");
            Console.WriteLine();
            Console.WriteLine("Subcommands:");
            Console.WriteLine("  check     Check for updates against a manifest");
            Console.WriteLine("  patch     Apply updates from a manifest");
            Console.WriteLine("  verify    Verify local files against a manifest");
            Console.WriteLine("  rollback  Restore from the most recent backup");
            Console.WriteLine("  cleanup   Remove leftover .hina.tmp/.bak files");
        }

        private static PatcherConfig LoadConfigOrDefault(string? configPath)
        {
            if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath)) return PatcherConfigLoader.Load(configPath);
            if (File.Exists("hina.config.json")) return PatcherConfigLoader.Load("hina.config.json");
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
