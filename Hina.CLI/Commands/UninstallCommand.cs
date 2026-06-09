using System;
using System.Threading.Tasks;
using Hina.PackageManager.Install;
using Microsoft.Extensions.Logging;

namespace Hina.CLI.Commands
{
    internal static class UninstallCommand
    {
        private static readonly System.Collections.Generic.HashSet<string> NoFlags =
            new(StringComparer.OrdinalIgnoreCase);

        public static async Task<int> RunAsync(CommandContext ctx, string[] args)
        {
            string? name = Args.FirstPositional(args, startIndex: 1);
            if (string.IsNullOrWhiteSpace(name))
            {
                ctx.Logger.LogError("Usage: hina uninstall <name>");
                return 2;
            }

            // uninstall takes no flags; reject typos instead of silently ignoring them.
            string? unknownFlag = Args.FirstUnknownFlag(args, NoFlags, startIndex: 1);
            if (unknownFlag != null)
            {
                ctx.Logger.LogError("Unknown flag '{Flag}'. Usage: hina uninstall <name>", unknownFlag);
                return 2;
            }

            UninstallService service = ctx.NewUninstallService();

            try
            {
                UninstallResult result = await service.UninstallAsync(name, ctx.Ct);
                if (!result.Removed)
                {
                    Console.WriteLine($"'{name}' was not installed.");
                    return 0;
                }
                Console.WriteLine($"Uninstalled {result.Name}.");
                return 0;
            }
            catch (Exception ex)
            {
                ctx.Logger.LogError("Uninstall failed: {Message}", ex.Message);
                return 2;
            }
        }
    }
}
