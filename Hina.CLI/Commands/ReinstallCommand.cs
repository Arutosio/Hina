using System;
using System.Threading.Tasks;
using Hina.PackageManager.Install;
using Microsoft.Extensions.Logging;

namespace Hina.CLI.Commands
{
    internal static class ReinstallCommand
    {
        public static async Task<int> RunAsync(CommandContext ctx, string[] args)
        {
            string? name = Args.FirstPositional(args, startIndex: 1);
            if (string.IsNullOrWhiteSpace(name))
            {
                ctx.Logger.LogError("Usage: hina reinstall <name> [--rotate-key]");
                return 2;
            }
            bool rotateKey = Args.HasFlag(args, "--rotate-key");

            ReinstallService service = ctx.NewReinstallService();

            try
            {
                InstallResult result = await service.ReinstallAsync(name, rotateKey, ctx.Ct);
                Console.WriteLine($"Reinstalled {result.Name} {result.Version} → {result.InstallPath}");
                return 0;
            }
            catch (Exception ex)
            {
                ctx.Logger.LogError("Reinstall failed: {Message}", ex.Message);
                return 2;
            }
        }
    }
}
