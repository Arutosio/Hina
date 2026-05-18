using System;
using System.Threading;
using System.Threading.Tasks;
using Hina.CLI.Commands;
using Microsoft.Extensions.Logging;

namespace Hina.CLI
{
    internal static class Program
    {
        private static Task<int> Main(string[] args)
        {
            if (args.Length == 0 || Args.HasFlag(args, "help") || Args.HasFlag(args, "--help") || Args.HasFlag(args, "-h"))
            {
                PrintHelp();
                return Task.FromResult(0);
            }

            bool verbose = Args.HasFlag(args, "--verbose") || Args.HasFlag(args, "-v");

            using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
            });

            ILogger logger = loggerFactory.CreateLogger("hina");
            CancellationToken ct = CancellationToken.None;

            string command = args[0].ToLowerInvariant();

            switch (command)
            {
                case "install":
                    return InstallCommand.RunAsync(args, logger, ct);
                case "uninstall":
                    return UninstallCommand.RunAsync(args, logger, ct);
                case "list":
                case "ls":
                    return Task.FromResult(ListCommand.Run(args));
                case "info":
                    return Task.FromResult(InfoCommand.Run(args, logger));
                case "which":
                    return Task.FromResult(WhichCommand.Run(args, logger));
                case "update":
                    return UpdateCommand.RunAsync(args, logger, ct);
                case "reinstall":
                    return ReinstallCommand.RunAsync(args, logger, ct);
                case "verify":
                    return VerifyCommand.RunAsync(args, logger, ct);
                case "dev":
                    return Task.FromResult(DevCommand.Run(args, loggerFactory, logger));
                default:
                    logger.LogError("Unknown command: {Cmd}", command);
                    PrintHelp();
                    return Task.FromResult(2);
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("Hina — cross-platform package manager");
            Console.WriteLine();
            Console.WriteLine("Usage: hina <command> [args...]");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("  install <url>          Install an app from a hina.app.json URL");
            Console.WriteLine("  uninstall <name>       Remove an installed app");
            Console.WriteLine("  list                   List installed apps");
            Console.WriteLine("  info <name>            Show details for an installed app");
            Console.WriteLine("  which <name>           Print the install path of an app");
            Console.WriteLine("  update [name]          Update one app or all installed apps");
            Console.WriteLine("  reinstall <name>       Reinstall an app (use --rotate-key for key change)");
            Console.WriteLine("  verify [name]          Reconcile registry against on-disk state (--repair to clean orphans)");
            Console.WriteLine("  dev <subcommand>       Advanced patcher commands");
            Console.WriteLine();
            Console.WriteLine("Global flags:");
            Console.WriteLine("  -v, --verbose          Enable debug logging");
            Console.WriteLine("  --allow-insecure       Permit HTTP descriptor URLs (install only)");
        }
    }
}
