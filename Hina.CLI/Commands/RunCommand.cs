using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Registry;
using Hina.PackageManager.Sandbox;
using Microsoft.Extensions.Logging;

namespace Hina.CLI.Commands
{
    // `hina run <app> [entryId] [-- appArgs...]`
    // The launch chokepoint: shell shortcuts for sandboxed apps point here instead
    // of at the app binary, so Hina can install the filesystem sandbox before exec.
    internal static class RunCommand
    {
        public static async Task<int> RunAsync(CommandContext ctx, string[] args)
        {
            List<string> positionals = new List<string>();
            List<string> appArgs = new List<string>();
            bool afterSep = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (!afterSep && args[i] == "--") { afterSep = true; continue; }
                (afterSep ? appArgs : positionals).Add(args[i]);
            }

            if (positionals.Count == 0)
            {
                ctx.Logger.LogError("Usage: hina run <app> [entryId] [-- args...]");
                return 2;
            }

            string name = positionals[0];
            string? entryId = positionals.Count > 1 ? positionals[1] : null;

            Registry registry = await ctx.LoadRegistryLockedAsync();
            if (!registry.Apps.TryGetValue(name, out InstalledApp? app))
            {
                ctx.Logger.LogError("'{Name}' is not installed.", name);
                return 1;
            }

            string descPath = ctx.Paths.DescriptorCache(name);
            if (!File.Exists(descPath))
            {
                ctx.Logger.LogError("Cached descriptor for '{Name}' is missing; cannot launch.", name);
                return 1;
            }
            AppDescriptor desc = DescriptorParser.Parse(await File.ReadAllTextAsync(descPath, ctx.Ct));

            string? execRel = ResolveExecRel(desc, entryId, out string? err);
            if (execRel == null)
            {
                ctx.Logger.LogError("{Error}", err);
                return 1;
            }
            string execAbs = Path.Combine(app.InstallPath, execRel);

            SandboxPlan plan = desc.Sandbox?.Enabled == true
                ? SandboxPlanner.Build(desc.Sandbox, app.UserGrants, app.InstallPath, SandboxEnv.FromSystem())
                : new SandboxPlan(unrestricted: true, new List<ResolvedFsRule>());

            ISandboxLauncher launcher = SandboxLauncherFactory.Current(ctx.Logger);
            return launcher.Launch(execAbs, appArgs, plan, ctx.Ct);
        }

        private static string? ResolveExecRel(AppDescriptor desc, string? entryId, out string? err)
        {
            err = null;
            if (entryId != null)
            {
                foreach (ShellEntry e in desc.Entries)
                {
                    if (string.Equals(e.Id, entryId, StringComparison.Ordinal)) return e.Exec;
                }
                err = $"entry '{entryId}' is not defined by this app.";
                return null;
            }
            if (desc.Entries.Count > 0)
            {
                return desc.Entries[0].Exec;
            }
            string? fromMap = OsExec(desc.Exec);
            if (fromMap == null) err = "app defines no entry or executable for this OS.";
            return fromMap;
        }

        private static string? OsExec(ExecMap exec)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return exec.Linux;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return exec.Macos;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return exec.Windows;
            return null;
        }
    }
}
