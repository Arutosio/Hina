using System;
using System.Linq;
using Hina.PackageManager.Paths;
using Hina.PackageManager.Registry;

namespace Hina.CLI.Commands
{
    internal static class ListCommand
    {
        public static int Run(string[] args)
        {
            InstallPaths paths = InstallPaths.ForCurrentOs();
            Registry registry = new RegistryStore(paths.RegistryFile).Load();

            if (registry.Apps.Count == 0)
            {
                Console.WriteLine("No apps installed.");
                return 0;
            }

            int nameWidth = Math.Max(4, registry.Apps.Keys.Max(k => k.Length));
            int verWidth = Math.Max(7, registry.Apps.Values.Max(a => a.InstalledVersion.Length));

            Console.WriteLine($"{"NAME".PadRight(nameWidth)}  {"VERSION".PadRight(verWidth)}  SOURCE");
            foreach (var kv in registry.Apps)
            {
                InstalledApp app = kv.Value;
                Console.WriteLine($"{app.Name.PadRight(nameWidth)}  {app.InstalledVersion.PadRight(verWidth)}  {app.BaseUrl}");
            }
            return 0;
        }
    }
}
