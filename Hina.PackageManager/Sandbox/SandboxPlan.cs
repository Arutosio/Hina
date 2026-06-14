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

        // True if the app's network access should be denied.
        //
        // Enforcement reality on Linux (Landlock):
        //   kernel < 6.7  (ABI < 4) : NOT enforced at all — app has full network
        //                              access; a WARNING is emitted once at launch.
        //   kernel 6.7+   (ABI >= 4) : TCP bind/connect are denied. UDP, ICMP/raw,
        //                              and path-based UNIX sockets are NOT covered by
        //                              Landlock and remain accessible (a WARNING
        //                              documents this gap).
        //   kernel 6.8+   (ABI >= 5) : additionally, abstract UNIX socket connections
        //                              are scoped. UDP/ICMP/raw remain outside
        //                              Landlock's reach.
        //
        // macOS (Seatbelt) and Windows (AppContainer) deny all network access when
        // this flag is set — Linux is more permissive due to Landlock limitations.
        //
        // Never set when Unrestricted is true (host opt-out).
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
