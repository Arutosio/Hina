using System;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Cli;
using Hina.Builder.Init;
using Microsoft.Extensions.Logging;

namespace Hina.Builder
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            if (args.Length == 0 || Args.HasFlag(args, "--help") || Args.HasFlag(args, "help"))
            {
                PrintHelp();
                return 0;
            }

            bool verbose = Args.HasFlag(args, "--verbose") || Args.HasFlag(args, "-v");

            using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
            });

            ILogger logger = loggerFactory.CreateLogger("Hina.Builder");

            string command = args[0].ToLowerInvariant();
            switch (command)
            {
                case "build":
                    return await BuildCommand.RunAsync(BuildOptions.FromArgs(args), logger, CancellationToken.None);
                case "keygen":
                    return KeygenCommand.Run(args, logger);
                case "init":
                    if (Console.IsInputRedirected)
                    {
                        logger.LogError("`init` is interactive and needs a terminal. In CI, use `build` + `hina dev sign-descriptor` instead.");
                        return 2;
                    }
                    return await InitCommand.RunAsync(args, new ConsolePrompt(), logger, CancellationToken.None);
                default:
                    logger.LogError("Unknown command: {Command}", command);
                    PrintHelp();
                    return 2;
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("Hina Builder");
            Console.WriteLine("Usage:");
            Console.WriteLine("  hina-builder init [--input <dir>]");
            Console.WriteLine("      Interactive wizard: scans the folder, asks a few questions, and generates a");
            Console.WriteLine("      signed hina.app.json plus the manifest/chunk store ready to host.");
            Console.WriteLine("  hina-builder build --input <dir> --out <dir> --base <url> [--platform <os[-arch]>] [--version v] [--chunk 65536] [--chunking fixed|cdc] [--min-chunk 2048] [--max-chunk 65536] [--avg-chunk 8192] [--sign-key key.b64] [-v|--verbose]");
            Console.WriteLine("      --platform writes manifest.<token>.json (one per variant) into a shared chunk store.");
            Console.WriteLine("  hina-builder keygen [--out <dir>] [--name <prefix>]");
        }
    }
}
