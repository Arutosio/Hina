using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Hina.Core.Configuration;
using Hina.Core.Patching;
using Hina.PackageManager.Diagnostics;
using Hina.PackageManager.Install;
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
            // `hina repair [name]` is `hina verify [name] --repair`.
            bool repair = Args.HasFlag(args, "--repair")
                          || args[0].Equals("repair", StringComparison.OrdinalIgnoreCase);
            bool deep = Args.HasFlag(args, "--deep");

            RegistryVerifier verifier = ctx.NewRegistryVerifier();
            bool suggestReinstall = false;
            bool repairable = false;

            try
            {
                List<AppDiagnostic> diags = verifier.Inspect(name);
                // Orphan artifacts (leftovers after a manual registry deletion) are global, not
                // tied to one app — only meaningful for a whole-system check.
                List<string> orphans = name == null ? verifier.FindOrphanArtifacts() : new List<string>();

                if (diags.Count == 0 && orphans.Count == 0)
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
                        repairable = true;
                    }
                    if (d.DanglingShellEntries.Count > 0 || d.DanglingHooks.Count > 0)
                    {
                        repairable = true;
                    }
                    if (d.DescriptorCacheMissing)
                    {
                        Console.WriteLine($"  - descriptor cache missing");
                        suggestReinstall = true;
                    }
                    foreach (string f in d.MissingFiles)
                    {
                        Console.WriteLine($"  - missing file: {f}");
                        suggestReinstall = true;
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

                if (orphans.Count > 0)
                {
                    problemCount++;
                    repairable = true;
                    Console.WriteLine($"orphaned artifacts (no registry entry): {orphans.Count}");
                    foreach (string o in orphans)
                    {
                        Console.WriteLine($"  - orphan: {o}");
                    }
                }

                // Deep check: re-fetch the manifest and hash every file (detects modified /
                // truncated content the offline check can't). Needs network.
                if (deep)
                {
                    Registry registry = await ctx.LoadRegistryLockedAsync();
                    foreach (AppDiagnostic d in diags)
                    {
                        if (d.AppDirMissing || !registry.Apps.TryGetValue(d.Name, out InstalledApp? row)) continue;
                        int broken = await DeepVerifyAsync(ctx, args, row);
                        if (broken < 0)
                        {
                            Console.WriteLine($"{d.Name}: could not deep-verify (offline or manifest unavailable).");
                        }
                        else if (broken > 0)
                        {
                            problemCount++;
                            suggestReinstall = true;
                            Console.WriteLine($"{d.Name}: {broken} file(s) corrupt or missing (hash check).");
                        }
                        else
                        {
                            Console.WriteLine($"{d.Name} — deep check OK");
                        }
                    }
                }

                if (problemCount == 0)
                {
                    return 0;
                }

                if (suggestReinstall)
                {
                    Console.WriteLine();
                    Console.WriteLine("Some apps are missing files — run `hina reinstall <app>` to restore them.");
                }

                if (!repair)
                {
                    if (repairable)
                    {
                        Console.WriteLine();
                        Console.WriteLine("Run `hina repair` to remove orphaned entries and dangling side-effects.");
                    }
                    return 1;
                }

                Console.WriteLine();
                Console.WriteLine("Repairing...");
                List<AppRepairResult> repaired = await verifier.RepairAsync(name, ctx.Ct);

                if (name == null)
                {
                    List<string> removedOrphans = await verifier.RepairOrphanArtifactsAsync(ctx.Ct);
                    foreach (string o in removedOrphans)
                    {
                        Console.WriteLine($"  removed orphan artifact: {o}");
                    }
                }

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

        // Manifest hash verification for one app. Returns the broken-file count, 0 if all
        // good, or -1 if the manifest couldn't be reached (offline / server down).
        private static async Task<int> DeepVerifyAsync(CommandContext ctx, string[] args, InstalledApp app)
        {
            try
            {
                PatcherConfig cfg = NetworkArgs.FromArgs(args)
                    .ToPatchConfig(app.BaseUrl, app.Channel, app.PublicKey, backup: false);
                using PatchClient client = new PatchClient(cfg);
                VerifyResult res = await client.VerifyAsync(app.InstallPath, ctx.Ct);
                return res.Success ? 0 : res.BrokenFiles.Count;
            }
            catch
            {
                return -1;
            }
        }
    }
}
