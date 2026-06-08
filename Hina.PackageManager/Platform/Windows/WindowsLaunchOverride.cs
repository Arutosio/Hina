namespace Hina.PackageManager.Platform.Windows
{
    // Splits InstallService's opaque sandbox launch override (`"<hinaExe>" run <app>
    // "<entry>"`) into the executable + argument string a .lnk shortcut stores separately.
    // Pure string logic so it is host-runnable in tests; the COM .lnk write that consumes
    // it is Windows-only.
    public readonly struct WindowsLaunchOverride
    {
        public string Exe { get; }
        public string Arguments { get; }

        private WindowsLaunchOverride(string exe, string arguments)
        {
            Exe = exe;
            Arguments = arguments;
        }

        public static WindowsLaunchOverride Parse(string launchOverride)
        {
            string s = launchOverride.Trim();
            if (s.Length == 0)
            {
                return new WindowsLaunchOverride(string.Empty, string.Empty);
            }

            if (s[0] == '"')
            {
                int close = s.IndexOf('"', 1);
                if (close > 0)
                {
                    string exe = s.Substring(1, close - 1);
                    string args = s.Substring(close + 1).Trim();
                    return new WindowsLaunchOverride(exe, args);
                }
                // Unbalanced quote — treat the rest verbatim as the exe.
                return new WindowsLaunchOverride(s.Substring(1), string.Empty);
            }

            int sp = s.IndexOfAny(new[] { ' ', '\t' });
            if (sp < 0)
            {
                return new WindowsLaunchOverride(s, string.Empty);
            }
            return new WindowsLaunchOverride(s.Substring(0, sp), s.Substring(sp + 1).Trim());
        }
    }
}
