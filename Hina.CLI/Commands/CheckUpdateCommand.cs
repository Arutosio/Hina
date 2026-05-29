using System;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager;
using Hina.PackageManager.Install;
using Hina.PackageManager.Net;
using Microsoft.Extensions.Logging;

namespace Hina.CLI.Commands
{
    // `hina check-update` (alias `hina check update`). Asks GitHub for the latest release and
    // reports whether the running build is current. Exit codes are script-friendly:
    //   0  = up to date
    //   10 = update available
    //   2  = check failed (offline, rate-limited, etc.)
    internal static class CheckUpdateCommand
    {
        public static async Task<int> RunAsync(CommandContext ctx, string[] args)
        {
            UpdateCheckResult result = await SelfUpdate.CheckAsync(
                HinaVersion.Current,
                async ct => await SharedHttp.Instance.GetStringAsync(SelfUpdate.LatestReleaseApiUrl, ct),
                ctx.Ct);

            switch (result.Status)
            {
                case UpdateAvailability.UpToDate:
                    Console.WriteLine($"hina {result.CurrentVersion} is up to date.");
                    return 0;

                case UpdateAvailability.UpdateAvailable:
                    Console.WriteLine($"Update available: {result.CurrentVersion} -> {result.LatestVersion}");
                    Console.WriteLine("Update with:");
                    Console.WriteLine("  curl -fsSL https://raw.githubusercontent.com/Arutosio/Hina/master/scripts/install.sh | bash");
                    return 10;

                default:
                    ctx.Logger.LogError("Could not check for updates: {Error}", result.Error ?? "unknown error");
                    return 2;
            }
        }
    }
}
