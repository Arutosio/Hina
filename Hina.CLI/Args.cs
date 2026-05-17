using System;

namespace Hina.CLI
{
    internal static class Args
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

        // First positional argument that isn't a flag/value (skips known flags + their values).
        public static string? FirstPositional(string[] args, int startIndex = 0)
        {
            for (int i = startIndex; i < args.Length; i++)
            {
                string a = args[i];
                if (a.StartsWith("-")) continue;
                return a;
            }
            return null;
        }
    }
}
