using System;
using System.Collections.Generic;

namespace Hina.Core.Cli
{
    // Shared command-line arg helpers used by Hina.CLI and Hina.Builder. All flags are
    // either boolean `--flag` or `--name value` pairs; which flags take a value is
    // app-specific, so callers pass their own valued-flag set where it matters.
    public static class Args
    {
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

        public static int ParseInt(string? value, int fallback)
        {
            return int.TryParse(value, out int v) ? v : fallback;
        }

        // First token that looks like a flag (starts with '-') but isn't in the known set.
        // Lets a command reject typos (e.g. `--allow-insecue`) instead of silently ignoring
        // them — a silently-dropped `--allow-insecure` would change security behavior with no
        // diagnostic.
        //
        // BUG-024: when `valuedFlags` is supplied, a token that is a known valued flag causes
        // the immediately following token (its value) to be skipped rather than inspected.
        // Without this, a value that starts with '-' (e.g. `--name -x`) was falsely reported
        // as an unknown flag.  Pass null (or omit) to preserve the original behaviour for
        // callers that have no valued flags.
        public static string? FirstUnknownFlag(string[] args, HashSet<string> known,
            int startIndex = 0, HashSet<string>? valuedFlags = null)
        {
            for (int i = startIndex; i < args.Length; i++)
            {
                string a = args[i];
                if (!a.StartsWith("-", StringComparison.Ordinal)) continue;
                if (!known.Contains(a)) return a;
                // Skip the value token of a known valued flag so a value that starts with '-'
                // is never mistaken for an unknown flag.
                if (valuedFlags != null && valuedFlags.Contains(a) && i + 1 < args.Length) i++;
            }
            return null;
        }

        // First positional argument that isn't a flag/value. `valuedFlags` lists the flags
        // that consume the following token as their value; FirstPositional must skip both
        // the flag AND its value, or a flag placed before the positional (e.g.
        // `hina install --retries 3 <url>`) would return the value ("3") as the positional.
        public static string? FirstPositional(string[] args, HashSet<string> valuedFlags, int startIndex = 0)
        {
            for (int i = startIndex; i < args.Length; i++)
            {
                string a = args[i];
                if (a.StartsWith("-"))
                {
                    if (valuedFlags.Contains(a) && i + 1 < args.Length) i++;
                    continue;
                }
                return a;
            }
            return null;
        }
    }
}
