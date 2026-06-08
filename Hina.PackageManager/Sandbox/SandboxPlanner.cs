using System.Collections.Generic;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Registry;

namespace Hina.PackageManager.Sandbox
{
    // Folds the descriptor's declared filesystem rules and the user's extra
    // runtime grants into one resolved plan. The app's own install dir is always
    // granted read-only. A "host" rule short-circuits to Unrestricted. When the
    // same concrete path appears more than once, read-write wins over read-only.
    public static class SandboxPlanner
    {
        public static SandboxPlan Build(SandboxSpec spec, IEnumerable<FsGrant> userGrants, string appDir, SandboxEnv env)
        {
            Dictionary<string, bool> byPath = new Dictionary<string, bool>(); // path -> canWrite

            void Add(string? path, bool canWrite)
            {
                if (string.IsNullOrEmpty(path))
                {
                    return;
                }
                byPath[path] = byPath.TryGetValue(path, out bool existing) ? existing || canWrite : canWrite;
            }

            // App dir always readable (exec implied by the backend).
            Add(appDir, canWrite: false);

            bool unrestricted = false;
            foreach (FsRule rule in spec.Filesystem)
            {
                if (rule.Path == SandboxTokens.Host)
                {
                    unrestricted = true;
                    continue;
                }
                Add(SandboxPaths.Resolve(rule.Path, appDir, env), rule.Access == "rw");
            }

            foreach (FsGrant grant in userGrants)
            {
                Add(grant.Path, grant.Access == "rw");
            }

            List<ResolvedFsRule> rules = new List<ResolvedFsRule>();
            foreach (KeyValuePair<string, bool> kv in byPath)
            {
                rules.Add(new ResolvedFsRule(kv.Key, kv.Value));
            }

            return new SandboxPlan(unrestricted, rules);
        }
    }
}
