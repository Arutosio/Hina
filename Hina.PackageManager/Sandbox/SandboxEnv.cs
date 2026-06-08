using System;
using System.IO;

namespace Hina.PackageManager.Sandbox
{
    // Resolved user-directory roots that abstract sandbox tokens map onto.
    // Constructed explicitly in tests for determinism; FromSystem() reads the
    // live environment (XDG vars with home-relative fallbacks).
    public readonly struct SandboxEnv
    {
        public string Home { get; }
        public string Documents { get; }
        public string Download { get; }
        public string Config { get; }
        public string Tmp { get; }

        public SandboxEnv(string home, string documents, string download, string config, string tmp)
        {
            Home = home;
            Documents = documents;
            Download = download;
            Config = config;
            Tmp = tmp;
        }

        public static SandboxEnv FromSystem()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string config = EnvOr("XDG_CONFIG_HOME", Path.Combine(home, ".config"));
            return new SandboxEnv(
                home: home,
                documents: EnvOr("XDG_DOCUMENTS_DIR", Path.Combine(home, "Documents")),
                download: EnvOr("XDG_DOWNLOAD_DIR", Path.Combine(home, "Downloads")),
                config: config,
                tmp: Path.GetTempPath());
        }

        private static string EnvOr(string var, string fallback)
        {
            string? v = Environment.GetEnvironmentVariable(var);
            return string.IsNullOrEmpty(v) ? fallback : v;
        }
    }
}
