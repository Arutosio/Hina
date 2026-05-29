using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Hina.PackageManager.Diagnostics;
using Hina.PackageManager.Registry;
using Microsoft.Extensions.Logging;

namespace Hina.CLI.Commands
{
    // `hina verify [name] [--repair]`.
    // Default: read-only diagnostic. With --repair: prune orphan registry entries
    // and dangling side-effects.
    internal static class VerifyCommand
    {
        public static async Task<int> RunAsync(CommandContext ctx, string[] args)
        {
            string? name = Args.FirstPositional(args, startIndex: 1);
            bool repair = Args.HasFlag(args, "--repair");

            RegistryVerifier verifier = ctx.NewRegistryVerifier();

            try
            {
                List<AppDiagnostic> diags = verifier.Inspect(name);

                if (diags.Count == 0)
                {
                    Console.WriteLine(name != null
                        ? $"'{name}' is not in the registry."
                        : "No apps installed; nothing to verify.");
                    return name != null ? 1 : 0;
                }

                int problemCount = 0;
                foreach (AppDiagnostic d in diags)
                {
                    if (d.IsHealthy)
                    {
                        Console.WriteLine($"{d.Name} ({d.Version}) — OK");
                        continue;
                    }

                    problemCount++;
                    Console.WriteLine($"{d.Name} ({d.Version})");
                    Console.WriteLine($"  install path: {d.InstallPath}");
                    if (d.AppDirMissing)
                    {
                        Console.WriteLine($"  STATUS: app directory missing");
                    }
                    foreach (ShellEntryRecord entry in d.DanglingShellEntries)
                    {
                        Console.WriteLine($"  - dangling: shell entry {entry.Evidence}");
                    }
                    foreach (HookEvidence ev in d.DanglingHooks)
                    {
                        Console.WriteLine($"  - dangling: {ev.Action} {ev.Evidence}");
                    }
                }

                if (problemCount == 0)
                {
                    return 0;
                }

                if (!repair)
                {
                    Console.WriteLine();
                    Console.WriteLine("Run `hina verify --repair` to remove orphaned entries and side-effects.");
                    return 1;
                }

                Console.WriteLine();
                Console.WriteLine("Repairing...");
                List<AppRepairResult> repaired = await verifier.RepairAsync(name, ctx.Ct);

                int healed = 0;
                foreach (AppRepairResult r in repaired)
                {
                    if (r.RemovedOrphanEntry)
                    {
                        Console.WriteLine($"  removed orphan: {r.Name} (entry + {r.RemovedHooks.Count} hooks + {r.RemovedShellEntries.Count} shell entries)");
                        healed++;
                    }
                    else if (r.RemovedHooks.Count > 0 || r.RemovedShellEntries.Count > 0)
                    {
                        Console.WriteLine($"  cleaned: {r.Name} ({r.RemovedHooks.Count} hooks, {r.RemovedShellEntries.Count} shell entries)");
                        healed++;
                    }
                }
                Console.WriteLine($"Repair complete. {healed} app(s) cleaned.");
                return 0;
            }
            catch (Exception ex)
            {
                ctx.Logger.LogError("Verify failed: {Message}", ex.Message);
                return 2;
            }
        }
    }
}
