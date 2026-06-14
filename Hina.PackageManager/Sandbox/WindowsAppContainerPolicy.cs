using System.Collections.Generic;
using System.Text;

namespace Hina.PackageManager.Sandbox
{
    // The access level granted to the AppContainer SID on a path. ReadExecute lets the
    // app read + run files (load DLLs, read data); ReadWriteExecute adds modify/create.
    public enum AppContainerAccess
    {
        ReadExecute,
        ReadWriteExecute,
    }

    // One ACE to add to a path's DACL: grant the per-app AppContainer SID `Access` on `Path`.
    public readonly struct AppContainerAce
    {
        public string Path { get; }
        public AppContainerAccess Access { get; }

        public AppContainerAce(string path, AppContainerAccess access)
        {
            Path = path;
            Access = access;
        }
    }

    // Pure policy core of the Windows AppContainer sandbox backend — the analogue of
    // MacOsSeatbeltProfile. Decides, from an OS-agnostic SandboxPlan: the stable container
    // moniker, the list of (path, access) ACEs to grant the container SID, and the
    // capability SIDs to attach. String/struct only, so it is unit-testable on any host;
    // the raw CreateProcess / ACL P/Invoke that consumes it lives in WindowsSandbox and is
    // proven by the windows-latest CI probe.
    //
    // Isolation model: an AppContainer process is DENIED every securable object unless its
    // DACL grants the AppContainer SID (or a capability SID, or ALL APPLICATION PACKAGES).
    // System DLL dirs (System32, WinSxS, the GAC) already carry an ALL APPLICATION PACKAGES
    // ACE granting read+execute to every container, so we do NOT — and must not, it needs
    // admin and edits a shared system ACL — add ACEs there. We grant only the app's own dir
    // (so it can load its binary) and each plan rule. Everything else stays denied by default.
    public static class WindowsAppContainerPolicy
    {
        // Well-known capability SID for outbound internet access. Attaching it to the
        // SECURITY_CAPABILITIES lets the AppContainer open client sockets; omitting it
        // denies network — mirroring Landlock/Seatbelt's RestrictNetwork.
        public const string InternetClientCapabilitySid = "S-1-15-3-1";

        // AppContainer monikers are capped at 64 wide chars and a constrained charset.
        private const int MaxContainerNameLength = 64;
        private const string ContainerPrefix = "Hina.";

        // Length of the hex hash suffix appended to every moniker (8 hex digits = 32-bit FNV-1a).
        // Reserved BEFORE consuming the budget with the basename so the discriminant is never
        // truncated even for very long app names.
        private const int HashSuffixLength = 9; // '.' + 8 hex chars

        // Stable per-app container name that is UNIQUE per install path.
        //
        // Format: "Hina.<sanitized-basename>.<hex8>"
        //   <hex8>  — lower-hex FNV-1a 32-bit hash of the canonicalized anchorPath
        //             (Path.GetFullPath + lower-invariant + trailing-separator stripped).
        //             Deterministic and cross-process-stable (unlike String.GetHashCode,
        //             which is randomised per process in .NET 5+).
        //
        // Two apps with the same exe basename but different install paths produce different
        // monikers → different AppContainer SIDs → their additive DACL grants never overlap
        // (BUG-009 fix). Same (basename, anchorPath) always yields the same moniker, so the
        // profile created at first launch is transparently reused on subsequent launches.
        //
        // anchorPath should be the unique install directory of the app (rules[0].Path in the
        // SandboxPlan; see WindowsSandbox.DeriveAnchorPath for the fallback chain).
        public static string ContainerName(string appName, string anchorPath)
        {
            // Canonicalize the anchor: full path, lower-invariant (Windows paths are
            // case-insensitive), no trailing directory separator — ensures the same
            // physical directory always hashes identically regardless of how the caller
            // spells it.
            // Windows paths always use '\' (and tolerate '/'); trim both literally
            // rather than relying on the host platform's separator chars, so the
            // canonicalisation is identical when this logic/tests run on non-Windows CI.
            string canon = anchorPath.TrimEnd('\\', '/').ToLowerInvariant();

            uint hash = Fnv1a32(canon);
            string hashSuffix = "." + hash.ToString("x8"); // e.g. ".a1b2c3d4"

            // Budget: MaxContainerNameLength minus prefix minus hash suffix.
            int basenameBudget = MaxContainerNameLength - ContainerPrefix.Length - HashSuffixLength;

            StringBuilder sb = new StringBuilder(MaxContainerNameLength);
            sb.Append(ContainerPrefix);
            int used = 0;
            foreach (char c in appName)
            {
                if (used >= basenameBudget) break;
                if (char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_')
                    sb.Append(c);
                else
                    sb.Append('_');
                used++;
            }
            sb.Append(hashSuffix);
            return sb.ToString();
        }

        // FNV-1a 32-bit — deterministic, cross-process-stable, no runtime randomisation.
        // https://datatracker.ietf.org/doc/html/draft-eastlake-fnv
        private static uint Fnv1a32(string s)
        {
            uint hash = 2166136261u; // FNV offset basis
            foreach (char c in s)
            {
                // Process each UTF-16 code unit as two bytes (little-endian) so that the
                // hash covers all characters, including non-ASCII in paths.
                hash = (hash ^ (byte)(c & 0xFF)) * 16777619u;
                hash = (hash ^ (byte)(c >> 8)) * 16777619u;
            }
            return hash;
        }

        // One ACE per plan rule, in order (rules[0] is conventionally the app dir, which
        // MUST be granted or the container can't read its own binary). ro → ReadExecute,
        // rw → ReadWriteExecute. Unrestricted (host opt-out) → no container, no ACEs.
        public static IReadOnlyList<AppContainerAce> BuildAceList(SandboxPlan plan)
        {
            if (plan.Unrestricted)
            {
                return System.Array.Empty<AppContainerAce>();
            }

            List<AppContainerAce> aces = new List<AppContainerAce>(plan.Rules.Count);
            foreach (ResolvedFsRule rule in plan.Rules)
            {
                aces.Add(new AppContainerAce(
                    rule.Path,
                    rule.CanWrite ? AppContainerAccess.ReadWriteExecute : AppContainerAccess.ReadExecute));
            }
            return aces;
        }

        // Capability SIDs to attach to the AppContainer. Network is denied by default and
        // allowed back only when the plan permits it.
        public static IReadOnlyList<string> BuildCapabilitySids(SandboxPlan plan)
        {
            if (plan.RestrictNetwork)
            {
                return System.Array.Empty<string>();
            }
            return new[] { InternetClientCapabilitySid };
        }
    }
}
