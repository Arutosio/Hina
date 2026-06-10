using System;
using System.Threading.Tasks;
using Hina.PackageManager.Install;
using Microsoft.Extensions.Logging;

namespace Hina.CLI.Commands
{
    internal static class ReinstallCommand
    {
        private static readonly System.Collections.Generic.HashSet<string> KnownFlags =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal) { "--rotate-key" };

        public static async Task<int> RunAsync(CommandContext ctx, string[] args)
        {
            // A typo'd `--rotate-keys` silently dropped the flag and changed key-rotation
            // behavior with no diagnostic.
            string? unknownFlag = Args.FirstUnknownFlag(args, KnownFlags, startIndex: 1);
            if (unknownFlag != null)
            {
                ctx.Logger.LogError("Unknown flag '{Flag}'. Usage: hina reinstall <name> [--rotate-key]", unknownFlag);
                return 2;
            }

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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Ctrl+C must reach CommandRouter's "Cancelled." path (exit 1), not be
                // reported as a failed reinstall.
                ctx.Logger.LogError("Reinstall failed: {Message}", ex.Message);
                return 2;
            }
        }
    }
}
