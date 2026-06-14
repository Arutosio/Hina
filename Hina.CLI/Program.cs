using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Hina.CLI
{
    internal static class Program
    {
        // BUG-012 / BUG-026: anchor global help/version detection to the first token only.
        //
        // Before this fix, Args.HasFlag scanned every token in the array, so any subcommand
        // whose positional argument happened to equal "help", "--help", "-h", "--version", or
        // "-V" (e.g. `hina uninstall help`, `hina run --version`) was hijacked into the global
        // short-circuit path and never dispatched to the real command.
        //
        // Rules after the fix:
        //   • "help"           — verb: only at args[0] (case-insensitive).
        //   • "--help" / "-h"  — flags: only at args[0]; they are not meaningful deep in an
        //                        argument list at the Program level (per-command help is handled
        //                        inside DevCommand; other commands surface a usage error).
        //   • "--version" / "-V" — only at args[0]; Ordinal comparison preserves the existing
        //                          -V (version) vs -v (verbose) case distinction.
        //   • "--verbose" / "-v" — intentional global modifier, left as whole-array scan.
        //
        // Extracted as an internal helper so Hina.CLI.Tests can exercise it without I/O.
        internal static bool TryGlobalShortCircuit(string[] args, out int exitCode)
        {
            // No args, or first token is a help indicator.
            if (args.Length == 0)
            {
                exitCode = -1; // caller prints help and returns 0
                return true;
            }

            string first = args[0];

            bool wantsHelp =
                string.Equals(first, "help",   StringComparison.OrdinalIgnoreCase) ||
                string.Equals(first, "--help", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(first, "-h",     StringComparison.OrdinalIgnoreCase);

            if (wantsHelp)
            {
                exitCode = 0;
                return true;
            }

            bool wantsVersion =
                string.Equals(first, "--version", StringComparison.Ordinal) ||
                string.Equals(first, "-V",        StringComparison.Ordinal);

            if (wantsVersion)
            {
                exitCode = 1; // sentinel: caller runs VersionCommand.Run()
                return true;
            }

            exitCode = -1;
            return false;
        }

        private static async Task<int> Main(string[] args)
        {
            if (TryGlobalShortCircuit(args, out int shortExitCode))
            {
                if (shortExitCode == 1)
                    return Commands.VersionCommand.Run();
                Help.PrintMain();
                return 0;
            }

            bool verbose = Args.HasFlag(args, "--verbose") || Args.HasFlag(args, "-v");

            using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
            });

            ILogger logger = loggerFactory.CreateLogger("hina");

            // B2: wire Ctrl-C to cooperative cancellation. First press signals the
            // CancellationToken so install/update/etc. can roll back cleanly; a second
            // press lets the runtime's default handler kill the process (escape hatch
            // if cancellation is stuck).
            using CancellationTokenSource cts = new CancellationTokenSource();
            bool cancelOnce = false;
            Console.CancelKeyPress += (s, e) =>
            {
                if (cancelOnce) return;       // let the second Ctrl-C terminate
                cancelOnce = true;
                e.Cancel = true;
                logger.LogWarning("Cancellation requested. Press Ctrl-C again to force-exit.");
                cts.Cancel();
            };
            CancellationToken ct = cts.Token;

            CommandContext context = CommandContext.ForCurrentOs(logger, loggerFactory, ct);
            return await CommandRouter.DispatchAsync(context, args);
        }
    }
}
