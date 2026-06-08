using System;
using System.IO;
using System.Threading.Tasks;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Install;
using Hina.PackageManager.Registry;
using Hina.PackageManager.Sandbox;
using Microsoft.Extensions.Logging;

namespace Hina.CLI.Commands
{
    internal static class InfoCommand
    {
        public static async Task<int> RunAsync(CommandContext ctx, string[] args)
        {
            string? name = Args.FirstPositional(args, startIndex: 1);
            if (string.IsNullOrWhiteSpace(name))
            {
                ctx.Logger.LogError("Usage: hina info <name>");
                return 2;
            }

            // #7: read under the registry lock (see ListCommand).
            Registry registry = await ctx.LoadRegistryLockedAsync();

            if (!registry.Apps.TryGetValue(name, out InstalledApp? app))
            {
                Console.WriteLine($"'{name}' is not installed.");
                return 1;
            }

            string installSuffix = Directory.Exists(app.InstallPath) ? "" : "   [missing]";

            Console.WriteLine($"Name:           {app.Name}");
            Console.WriteLine($"Version:        {app.InstalledVersion}");
            Console.WriteLine($"Channel:        {app.Channel}");
            Console.WriteLine($"Install path:   {app.InstallPath}{installSuffix}");
            Console.WriteLine($"Descriptor URL: {app.DescriptorUrl}");
            Console.WriteLine($"Base URL:       {app.BaseUrl}");
            Console.WriteLine($"Public key fpr: {InstallService.ComputeFingerprint(app.PublicKey)}");
            Console.WriteLine($"Installed at:   {app.InstalledAt:u}");
            Console.WriteLine($"Last updated:   {app.LastUpdatedAt:u}");

            if (app.ShellEntries.Count > 0)
            {
                Console.WriteLine("Shell entries:");
                foreach (ShellEntryRecord e in app.ShellEntries) Console.WriteLine($"  - [{e.Id}] {e.Evidence}");
            }
            if (app.ExecutedHooks.Count > 0)
            {
                Console.WriteLine("Hooks:");
                foreach (HookEvidence h in app.ExecutedHooks) Console.WriteLine($"  - {h.Action}: {h.Evidence}");
            }
            // Sandbox / permission summary, from the cached descriptor (best-effort:
            // a missing or corrupt cache just omits the block — info must not fail).
            string descPath = ctx.Paths.DescriptorCache(name);
            if (File.Exists(descPath))
            {
                try
                {
                    AppDescriptor desc = DescriptorParser.Parse(await File.ReadAllTextAsync(descPath, ctx.Ct));
                    AppPermissions perms = AppPermissions.From(desc, app);
                    Console.Write(PermissionsFormatter.Compact(perms));
                }
                catch (Exception ex)
                {
                    ctx.Logger.LogDebug(ex, "Could not render sandbox summary for {Name}", name);
                }
            }

            if (!string.IsNullOrEmpty(installSuffix))
            {
                Console.WriteLine();
                Console.WriteLine("Install directory is missing. Run `hina verify --repair` to clean up registry + side-effects.");
            }
            return 0;
        }
    }
}
