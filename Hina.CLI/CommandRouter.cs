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
        public static Task<int> DispatchAsync(CommandContext ctx, string[] args)
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
                case "dev":
                    return Task.FromResult(DevCommand.Run(ctx, args));
                default:
                    ctx.Logger.LogError("Unknown command: {Cmd}", command);
                    Help.PrintMain();
                    return Task.FromResult(2);
            }
        }
    }
}
