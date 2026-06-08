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

        public SandboxPlan(bool unrestricted, IReadOnlyList<ResolvedFsRule> rules)
        {
            Unrestricted = unrestricted;
            Rules = rules;
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
