using System;
using System.Threading.Tasks;
using Hina.PackageManager.Registry;
using Microsoft.Extensions.Logging;

namespace Hina.CLI.Commands
{
    internal static class WhichCommand
    {
        public static async Task<int> RunAsync(CommandContext ctx, string[] args)
        {
            string? name = Args.FirstPositional(args, startIndex: 1);
            if (string.IsNullOrWhiteSpace(name))
            {
                ctx.Logger.LogError("Usage: hina which <name>");
                return 2;
            }

            // #7: read under the registry lock (see ListCommand).
            Registry registry = await ctx.LoadRegistryLockedAsync();

            if (!registry.Apps.TryGetValue(name, out InstalledApp? app))
            {
                Console.Error.WriteLine($"'{name}' is not installed.");
                return 1;
            }

            // We don't store the per-OS exec in the registry directly; descriptor cache has it.
            // For Phase 2 the install dir is enough; the user can grep within it.
            Console.WriteLine(app.InstallPath);
            return 0;
        }
    }
}
