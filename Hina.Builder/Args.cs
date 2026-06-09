using System;

namespace Hina.Builder
{
    // Tiny arg helpers shared by the builder commands. Kept deliberately minimal — the
    // builder's flags are all `--name value` or boolean `--flag`.
    internal static class Args
    {
        public static bool HasArg(string[] args, string name)
        {
            foreach (string arg in args)
            {
                if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        public static string? GetArgValue(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }
            return null;
        }

        public static int ParseInt(string? value, int fallback)
        {
            return int.TryParse(value, out int v) ? v : fallback;
        }
    }
}
