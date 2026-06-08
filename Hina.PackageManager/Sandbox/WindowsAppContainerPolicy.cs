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

        // Stable per-app container name, e.g. "Hina.signal". Deterministic so re-launches
        // derive the same SID (the profile is created once, then reused). Sanitized to the
        // AppContainer charset and capped at 64 chars.
        public static string ContainerName(string appName)
        {
            StringBuilder sb = new StringBuilder(MaxContainerNameLength);
            sb.Append(ContainerPrefix);
            foreach (char c in appName)
            {
                if (sb.Length >= MaxContainerNameLength) break;
                if (char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_') sb.Append(c);
                else sb.Append('_');
            }
            return sb.ToString();
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
