using System;

namespace Hina.CLI
{
    internal static class Help
    {
        public static void PrintMain()
        {
            Console.WriteLine("Hina — cross-platform package manager");
            Console.WriteLine();
            Console.WriteLine("Usage: hina <command> [args...]");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("  install <url>          Install an app from a hina.app.json URL");
            Console.WriteLine("  uninstall <name>       Remove an installed app");
            Console.WriteLine("  list                   List installed apps");
            Console.WriteLine("  info <name>            Show details for an installed app");
            Console.WriteLine("  which <name>           Print the install path of an app");
            Console.WriteLine("  update [name]          Update one app or all installed apps");
            Console.WriteLine("  reinstall <name>       Reinstall an app (use --rotate-key for key change)");
            Console.WriteLine("  verify [name]          Reconcile registry against on-disk state (--repair to clean orphans)");
            Console.WriteLine("  version                Print the installed Hina version");
            Console.WriteLine("  check-update           Check whether a newer Hina release is available");
            Console.WriteLine("  dev <subcommand>       Advanced patcher commands");
            Console.WriteLine();
            Console.WriteLine("Global flags:");
            Console.WriteLine("  -v, --verbose          Enable debug logging");
            Console.WriteLine("  --allow-insecure       Permit HTTP descriptor URLs (install only)");
            Console.WriteLine();
            Console.WriteLine("Network tuning (install + update; raise these on flaky / mobile connections):");
            Console.WriteLine("  --retries N              Max retry attempts per request (default 8)");
            Console.WriteLine("  --connect-timeout SEC    TCP connect timeout in seconds (default 10)");
            Console.WriteLine("  --request-timeout SEC    Overall request timeout in seconds (default 60)");
        }
    }
}
