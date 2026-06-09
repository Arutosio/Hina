using System;

namespace Hina.CLI
{
    internal static class Args
    {
        // Flags that consume the following token as their value. FirstPositional must skip
        // both the flag AND its value, or a flag placed before the positional (e.g.
        // `hina install --retries 3 <url>`) would return the value ("3") as the positional.
        private static readonly System.Collections.Generic.HashSet<string> ValuedFlags =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "--in", "--key", "--out", "--dir", "--base", "--config", "--pubkey",
                "--channel", "--jobs", "--retries", "--connect-timeout", "--request-timeout",
                "--grant", "--revoke"
            };

        public static bool HasFlag(string[] args, string name)
        {
            foreach (string arg in args)
            {
                if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static string? GetValue(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            return null;
        }

        // First token that looks like a flag (starts with '-') but isn't in the known set.
        // Lets a command reject typos (e.g. `--allow-insecue`) instead of silently ignoring
        // them — a silently-dropped `--allow-insecure` would change security behavior with no
        // diagnostic. Value tokens of valued flags don't start with '-', so they're skipped here.
        public static string? FirstUnknownFlag(string[] args, System.Collections.Generic.HashSet<string> known, int startIndex = 0)
        {
            for (int i = startIndex; i < args.Length; i++)
            {
                string a = args[i];
                if (!a.StartsWith("-", StringComparison.Ordinal)) continue;
                if (!known.Contains(a)) return a;
            }
            return null;
        }

        // First positional argument that isn't a flag/value (skips known flags + their values).
        public static string? FirstPositional(string[] args, int startIndex = 0)
        {
            for (int i = startIndex; i < args.Length; i++)
            {
                string a = args[i];
                if (a.StartsWith("-"))
                {
                    // Skip the value token belonging to a valued flag so it isn't mistaken
                    // for the positional argument.
                    if (ValuedFlags.Contains(a) && i + 1 < args.Length) i++;
                    continue;
                }
                return a;
            }
            return null;
        }
    }
}
