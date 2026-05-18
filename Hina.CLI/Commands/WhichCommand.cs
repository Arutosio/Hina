using System;
using System.IO;
using System.Runtime.InteropServices;
using Hina.PackageManager.Paths;
using Hina.PackageManager.Registry;
using Microsoft.Extensions.Logging;

namespace Hina.CLI.Commands
{
    internal static class WhichCommand
    {
        public static int Run(string[] args, ILogger logger)
        {
            string? name = Args.FirstPositional(args, startIndex: 1);
            if (string.IsNullOrWhiteSpace(name))
            {
                logger.LogError("Usage: hina which <name>");
                return 2;
            }

            InstallPaths paths = InstallPaths.ForCurrentOs();
            Registry registry = new RegistryStore(paths.RegistryFile).Load();

            if (!registry.Apps.TryGetValue(name, out InstalledApp? app))
            {
                Console.Error.WriteLine($"'{name}' is not installed.");
                return 1;
            }

            // We don't store the per-OS exec in the registry directly; descriptor cache has it.
            // For Phase 2 the install dir is enough; the user can grep within it.
            Console.WriteLine(app.InstallPath);
            return 0;
        }
    }
}
