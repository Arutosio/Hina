using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Hina.PackageManager.Install;
using Microsoft.Extensions.Logging;

namespace Hina.CLI.Commands
{
    internal static class UpdateCommand
    {
        public static async Task<int> RunAsync(CommandContext ctx, string[] args)
        {
            string? name = Args.FirstPositional(args, startIndex: 1);
            bool force = Args.HasFlag(args, "--force");
            bool allowDowngrade = Args.HasFlag(args, "--allow-downgrade");

            int jobs = 4;
            string? jobsArg = Args.GetValue(args, "--jobs");
            if (jobsArg != null && int.TryParse(jobsArg, out int parsed) && parsed > 0)
            {
                jobs = parsed;
            }

            UpdateService service = ctx.NewUpdateService();
            UpdateOptions options = new UpdateOptions
            {
                Force = force,
                AllowDowngrade = allowDowngrade,
                MaxParallelism = jobs,
                Network = NetworkArgs.FromArgs(args)
            };

            try
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    List<UpdateResult> results = await service.UpdateAllAsync(options, ctx.Ct);
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
                    UpdateResult result = await service.UpdateAsync(name, options, ctx.Ct);
                    PrintResult(result);
                    return result.Status == UpdateStatus.Failed ? 2 : 0;
                }
            }
            catch (Exception ex)
            {
                ctx.Logger.LogError("Update failed: {Message}", ex.Message);
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
