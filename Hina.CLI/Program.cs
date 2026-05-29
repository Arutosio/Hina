using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Hina.CLI
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            if (args.Length == 0 || Args.HasFlag(args, "help") || Args.HasFlag(args, "--help") || Args.HasFlag(args, "-h"))
            {
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
