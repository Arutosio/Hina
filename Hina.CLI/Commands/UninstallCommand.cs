using System;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Install;
using Hina.PackageManager.Paths;
using Hina.PackageManager.Platform;
using Microsoft.Extensions.Logging;

namespace Hina.CLI.Commands
{
    internal static class UninstallCommand
    {
        public static async Task<int> RunAsync(string[] args, ILogger logger, CancellationToken ct)
        {
            string? name = Args.FirstPositional(args, startIndex: 1);
            if (string.IsNullOrWhiteSpace(name))
            {
                logger.LogError("Usage: hina uninstall <name>");
                return 2;
            }

            InstallPaths paths = InstallPaths.ForCurrentOs();
            IPlatformIntegration platform = PlatformIntegrationFactory.Current(paths);
            UninstallService service = new UninstallService(paths, platform);

            try
            {
                UninstallResult result = await service.UninstallAsync(name, ct);
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
                logger.LogError("Uninstall failed: {Message}", ex.Message);
                return 2;
            }
        }
    }
}
