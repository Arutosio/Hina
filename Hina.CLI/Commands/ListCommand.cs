using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Hina.PackageManager.Registry;

namespace Hina.CLI.Commands
{
    internal static class ListCommand
    {
        public static async Task<int> RunAsync(CommandContext ctx, string[] args)
        {
            // #7: read under the registry lock so we don't observe a half-written file
            // while an install/update is mid-commit.
            Registry registry = await ctx.LoadRegistryLockedAsync();

            if (registry.Apps.Count == 0)
            {
                Console.WriteLine("No apps installed.");
                return 0;
            }

            int nameWidth = Math.Max(4, registry.Apps.Keys.Max(k => k.Length));
            int verWidth = Math.Max(7, registry.Apps.Values.Max(a => a.InstalledVersion.Length));

            Console.WriteLine($"{"NAME".PadRight(nameWidth)}  {"VERSION".PadRight(verWidth)}  SOURCE");
            bool anyMissing = false;
            foreach (var kv in registry.Apps)
            {
                InstalledApp app = kv.Value;
                string suffix = "";
                if (!Directory.Exists(app.InstallPath))
                {
                    suffix = "  [missing]";
                    anyMissing = true;
                }
                Console.WriteLine($"{app.Name.PadRight(nameWidth)}  {app.InstalledVersion.PadRight(verWidth)}  {app.BaseUrl}{suffix}");
            }
            if (anyMissing)
            {
                Console.WriteLine();
                Console.WriteLine("Some apps are missing their install directory. Run `hina verify --repair` to clean up.");
            }
            return 0;
        }
    }
}
