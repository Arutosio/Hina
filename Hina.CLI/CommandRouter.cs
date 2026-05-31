using System;
using System.Threading.Tasks;
using Hina.CLI.Commands;
using Microsoft.Extensions.Logging;

namespace Hina.CLI
{
    // Maps the first CLI verb to its command. Extracted from Program.Main so the routing is
    // testable in isolation: a test builds a CommandContext over a temp dir and dispatches
    // verbs without spawning a process. Help/cancellation wiring stays in Program.
    internal static class CommandRouter
    {
        public static async Task<int> DispatchAsync(CommandContext ctx, string[] args)
        {
            // Top-level safety net: commands surface expected failures via exit codes, but a
            // few paths throw (registry lock timeout, future-schema registry, unexpected IO —
            // notably the read-only commands which don't wrap their own body). Catch here so
            // the CLI exits with a clean message + code instead of an unhandled stack trace.
            try
            {
                return await DispatchCoreAsync(ctx, args);
            }
            catch (OperationCanceledException)
            {
                ctx.Logger.LogError("Cancelled.");
                return 1;
            }
            catch (Exception ex)
            {
                ctx.Logger.LogError("{Message}", ex.Message);
                return 2;
            }
        }

        private static Task<int> DispatchCoreAsync(CommandContext ctx, string[] args)
        {
            string command = args[0].ToLowerInvariant();

            switch (command)
            {
                case "install":
                    return InstallCommand.RunAsync(ctx, args);
                case "uninstall":
                    return UninstallCommand.RunAsync(ctx, args);
                case "list":
                case "ls":
                    return ListCommand.RunAsync(ctx, args);
                case "info":
                    return InfoCommand.RunAsync(ctx, args);
                case "which":
                    return WhichCommand.RunAsync(ctx, args);
                case "update":
                    return UpdateCommand.RunAsync(ctx, args);
                case "reinstall":
                    return ReinstallCommand.RunAsync(ctx, args);
                case "verify":
                    return VerifyCommand.RunAsync(ctx, args);
                case "version":
                    return Task.FromResult(VersionCommand.Run());
                case "check-update":
                    return CheckUpdateCommand.RunAsync(ctx, args);
                case "check":
                    // `hina check update` — accept the two-word form too.
                    if (args.Length > 1 && args[1].Equals("update", System.StringComparison.OrdinalIgnoreCase))
                    {
                        return CheckUpdateCommand.RunAsync(ctx, args);
                    }
                    ctx.Logger.LogError("Unknown command: check {Arg}", args.Length > 1 ? args[1] : "");
                    Help.PrintMain();
                    return Task.FromResult(2);
                case "dev":
                    return DevCommand.RunAsync(ctx, args);
                default:
                    ctx.Logger.LogError("Unknown command: {Cmd}", command);
                    Help.PrintMain();
                    return Task.FromResult(2);
            }
        }
    }
}
