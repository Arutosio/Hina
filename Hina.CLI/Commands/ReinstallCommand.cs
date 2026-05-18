using System;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Install;
using Hina.PackageManager.Paths;
using Hina.PackageManager.Platform;
using Microsoft.Extensions.Logging;

namespace Hina.CLI.Commands
{
    internal static class ReinstallCommand
    {
        public static async Task<int> RunAsync(string[] args, ILogger logger, CancellationToken ct)
        {
            string? name = Args.FirstPositional(args, startIndex: 1);
            if (string.IsNullOrWhiteSpace(name))
            {
                logger.LogError("Usage: hina reinstall <name> [--rotate-key]");
                return 2;
            }
            bool rotateKey = Args.HasFlag(args, "--rotate-key");

            InstallPaths paths = InstallPaths.ForCurrentOs();
            IPlatformIntegration platform = PlatformIntegrationFactory.Current(paths);
            ReinstallService service = new ReinstallService(paths, platform);

            try
            {
                InstallResult result = await service.ReinstallAsync(name, rotateKey, ct);
                Console.WriteLine($"Reinstalled {result.Name} {result.Version} → {result.InstallPath}");
                return 0;
            }
            catch (Exception ex)
            {
                logger.LogError("Reinstall failed: {Message}", ex.Message);
                return 2;
            }
        }
    }
}
