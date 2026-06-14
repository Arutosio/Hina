using System;
using System.Collections.Generic;
using CoreArgs = Hina.Core.Cli.Args;

namespace Hina.CLI
{
    // CLI-specific facade over the shared parser in Hina.Core.Cli.Args: holds the set of
    // flags that take a value so call-sites don't have to pass it around.
    internal static class Args
    {
        // Flags that consume the following token as their value. FirstPositional must skip
        // both the flag AND its value, or a flag placed before the positional (e.g.
        // `hina install --retries 3 <url>`) would return the value ("3") as the positional.
        private static readonly HashSet<string> ValuedFlags =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "--in", "--key", "--out", "--dir", "--base", "--config", "--pubkey",
                "--channel", "--jobs", "--retries", "--connect-timeout", "--request-timeout",
                "--grant", "--revoke"
            };

        public static bool HasFlag(string[] args, string name)
            => CoreArgs.HasFlag(args, name);

        public static string? GetValue(string[] args, string name)
            => CoreArgs.GetValue(args, name);

        public static string? FirstUnknownFlag(string[] args, HashSet<string> known, int startIndex = 0)
            // Pass ValuedFlags so the scan skips a valued flag's value token (e.g. the "-5" in
            // `--request-timeout -5`) instead of mis-reporting it as an unknown flag (BUG-036:
            // the BUG-024 fix added the Core parameter but the facade never supplied it).
            => CoreArgs.FirstUnknownFlag(args, known, startIndex, valuedFlags: ValuedFlags);

        public static string? FirstPositional(string[] args, int startIndex = 0)
            => CoreArgs.FirstPositional(args, ValuedFlags, startIndex);
    }
}
