using System.Collections.Generic;

namespace Hina.PackageManager.Sandbox
{
    // A concrete, resolved set of filesystem permissions for one app launch.
    // Consumed by an ISandboxLauncher backend (Landlock on Linux).
    public sealed class SandboxPlan
    {
        // True if a "host" rule was present — the backend should not restrict
        // the filesystem at all (other isolation, if any, may still apply).
        public bool Unrestricted { get; }

        public IReadOnlyList<ResolvedFsRule> Rules { get; }

        // True if the app's network access should be denied. Enforced on Linux via
        // Landlock net rules (ABI >= 4 / kernel 6.7+); declared-only on older
        // kernels and other OSes. Never set when Unrestricted (host opt-out).
        public bool RestrictNetwork { get; }

        public SandboxPlan(bool unrestricted, IReadOnlyList<ResolvedFsRule> rules, bool restrictNetwork = false)
        {
            Unrestricted = unrestricted;
            Rules = rules;
            RestrictNetwork = restrictNetwork;
        }
    }

    public sealed class ResolvedFsRule
    {
        public string Path { get; }
        public bool CanWrite { get; }

        public ResolvedFsRule(string path, bool canWrite)
        {
            Path = path;
            CanWrite = canWrite;
        }
    }
}
