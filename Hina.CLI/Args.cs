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
                "--channel", "--jobs", "--retries", "--connect-timeout", "--request-timeout"
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
