using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Hina.PackageManager.Install;
using Microsoft.Extensions.Logging;

namespace Hina.CLI.Commands
{
    internal static class UpdateCommand
    {
        // Flags `hina update` accepts; a typo (e.g. `--alow-downgrade`) must be rejected,
        // not silently ignored — same rule install/uninstall already enforce.
        private static readonly HashSet<string> KnownFlags = new HashSet<string>(StringComparer.Ordinal)
        {
            "--force", "--allow-downgrade", "--accept-new-permissions", "--jobs",
            "--allow-insecure", "--retries", "--connect-timeout", "--request-timeout"
        };

        public static async Task<int> RunAsync(CommandContext ctx, string[] args)
        {
            string? unknownFlag = Args.FirstUnknownFlag(args, KnownFlags, startIndex: 1);
            if (unknownFlag != null)
            {
                ctx.Logger.LogError("Unknown flag '{Flag}'. Usage: hina update [name] [--force] [--allow-downgrade] [--accept-new-permissions] [--jobs N]", unknownFlag);
                return 2;
            }

            string? name = Args.FirstPositional(args, startIndex: 1);
            bool force = Args.HasFlag(args, "--force");
            bool allowDowngrade = Args.HasFlag(args, "--allow-downgrade");
            bool acceptNewPerms = Args.HasFlag(args, "--accept-new-permissions");

            try
            {
                // --jobs absent → default 4. Present but not a positive integer → fail loudly
                // rather than silently using 4 (see NetworkArgs for the same rule).
                int jobs = 4;
                string? jobsArg = Args.GetValue(args, "--jobs");
                if (jobsArg != null)
                {
                    if (!int.TryParse(jobsArg, out int parsed) || parsed <= 0)
                    {
                        throw new FormatException($"Invalid value for --jobs: '{jobsArg}'. Expected a positive integer.");
                    }
                    jobs = parsed;
                }

                UpdateService service = ctx.NewUpdateService();
                UpdateOptions options = new UpdateOptions
                {
                    Force = force,
                    AllowDowngrade = allowDowngrade,
                    AcceptNewPermissions = acceptNewPerms,
                    MaxParallelism = jobs,
                    Network = NetworkArgs.FromArgs(args)
                };


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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Ctrl+C must reach CommandRouter's "Cancelled." path (exit 1), not be
                // reported as a failed update.
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
