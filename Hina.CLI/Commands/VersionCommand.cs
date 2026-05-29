using System;
using Hina.PackageManager.Install;

namespace Hina.CLI.Commands
{
    internal static class VersionCommand
    {
        public static int Run()
        {
            Console.WriteLine($"hina {HinaVersion.Current}");
            return 0;
        }
    }
}
