using Hina.PackageManager.Descriptor;

namespace Hina.PackageManager.Sandbox
{
    // Maps an abstract sandbox token to a concrete absolute path. Returns null
    // for the "host" token, which means "no filesystem restriction" rather than
    // a single path. Unknown tokens (already rejected by DescriptorValidator)
    // also return null and are ignored by the planner.
    public static class SandboxPaths
    {
        public static string? Resolve(string token, string appDir, SandboxEnv env)
        {
            return token switch
            {
                SandboxTokens.App => appDir,
                SandboxTokens.Home => env.Home,
                SandboxTokens.XdgDocuments => env.Documents,
                SandboxTokens.XdgDownload => env.Download,
                SandboxTokens.XdgConfig => env.Config,
                SandboxTokens.Tmp => env.Tmp,
                _ => null, // host or unknown
            };
        }
    }
}
