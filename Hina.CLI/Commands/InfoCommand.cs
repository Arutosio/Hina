using System;
using System.IO;
using Hina.PackageManager.Install;
using Hina.PackageManager.Paths;
using Hina.PackageManager.Registry;
using Microsoft.Extensions.Logging;

namespace Hina.CLI.Commands
{
    internal static class InfoCommand
    {
        public static int Run(string[] args, ILogger logger)
        {
            string? name = Args.FirstPositional(args, startIndex: 1);
            if (string.IsNullOrWhiteSpace(name))
            {
                logger.LogError("Usage: hina info <name>");
                return 2;
            }

            InstallPaths paths = InstallPaths.ForCurrentOs();
            Registry registry = new RegistryStore(paths.RegistryFile).Load();

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
            if (!string.IsNullOrEmpty(installSuffix))
            {
                Console.WriteLine();
                Console.WriteLine("Install directory is missing. Run `hina verify --repair` to clean up registry + side-effects.");
            }
            return 0;
        }
    }
}
