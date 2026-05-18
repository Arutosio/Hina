using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Install;
using Hina.PackageManager.Paths;
using Hina.PackageManager.Platform;
using Microsoft.Extensions.Logging;

namespace Hina.CLI.Commands
{
    internal static class UpdateCommand
    {
        public static async Task<int> RunAsync(string[] args, ILogger logger, CancellationToken ct)
        {
            string? name = Args.FirstPositional(args, startIndex: 1);
            bool force = Args.HasFlag(args, "--force");

            int jobs = 4;
            string? jobsArg = Args.GetValue(args, "--jobs");
            if (jobsArg != null && int.TryParse(jobsArg, out int parsed) && parsed > 0)
            {
                jobs = parsed;
            }

            InstallPaths paths = InstallPaths.ForCurrentOs();
            IPlatformIntegration platform = PlatformIntegrationFactory.Current(paths);
            UpdateService service = new UpdateService(paths, platform);
            UpdateOptions options = new UpdateOptions { Force = force, MaxParallelism = jobs };

            try
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    List<UpdateResult> results = await service.UpdateAllAsync(options, ct);
                    int failures = 0;
                    foreach (UpdateResult r in results)
                    {
                        PrintResult(r);
                        if (r.Status == UpdateStatus.Failed) failures++;
                    }
                    return failures == 0 ? 0 : 2;
                }
                else
                {
                    UpdateResult result = await service.UpdateAsync(name, options, ct);
                    PrintResult(result);
                    return result.Status == UpdateStatus.Failed ? 2 : 0;
                }
            }
            catch (Exception ex)
            {
                logger.LogError("Update failed: {Message}", ex.Message);
                return 2;
            }
        }

        private static void PrintResult(UpdateResult r)
        {
            switch (r.Status)
            {
                case UpdateStatus.Updated:
                    Console.WriteLine($"Updated {r.Name}: {r.FromVersion} → {r.ToVersion}");
                    break;
                case UpdateStatus.AlreadyUpToDate:
                    Console.WriteLine($"{r.Name} already up to date ({r.ToVersion}).");
                    break;
                case UpdateStatus.Failed:
                    Console.Error.WriteLine($"{r.Name}: {r.Message}");
                    break;
            }
        }
    }
}
