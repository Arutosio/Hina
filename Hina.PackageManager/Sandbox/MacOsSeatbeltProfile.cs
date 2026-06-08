using System.Collections.Generic;
using System.Text;

namespace Hina.PackageManager.Sandbox
{
    // Generates a Seatbelt (.sb) sandbox profile from a SandboxPlan, for use with
    // macOS `sandbox-exec -f <profile>`. Pure/string-only so it is unit-testable;
    // the actual enforcement lives in MacOsSandbox.
    //
    // Policy shape: deny everything by default, then allow back exactly what the
    // app needs — process exec/fork, a system read baseline (dyld cache + system
    // frameworks, without which nothing launches), the plan's resolved filesystem
    // rules (read for ro, read+write for rw), and network unless the plan denies it.
    public static class MacOsSeatbeltProfile
    {
        // Apple-shipped base sandbox profile, stable across macOS releases. Imported
        // so dyld and the mach bootstrap work; it does not grant broad file access.
        public const string BsdBaseProfile = "/System/Library/Sandbox/Profiles/bsd.sb";

        // System paths every macOS process must read to start (dyld, libs, frameworks,
        // ICU/locale data, timezone). Read-only — analogous to the Linux implicit
        // SystemRuntimePaths. These only relax the sandbox layer; POSIX perms still apply.
        public static readonly IReadOnlyList<string> SystemReadPaths = new[]
        {
            "/usr/lib",
            "/usr/bin",
            "/bin",
            "/usr/share",
            "/System",
            "/Library",
            "/private/var/db/dyld",
            "/dev/null",
            "/dev/random",
            "/dev/urandom",
        };

        public static string Build(SandboxPlan plan)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("(version 1)");
            // The BSD base profile grants the low-level operations (dyld shared-cache
            // reads, mach bootstrap, sysctl) without which dyld aborts the process
            // before main() — but it does NOT open up arbitrary file read/write, so
            // our deny-default + explicit subpath grants still isolate the app.
            sb.AppendLine("(import \"" + BsdBaseProfile + "\")");
            sb.AppendLine("(deny default)");

            // Let the app spawn/replace itself and read its own metadata.
            sb.AppendLine("(allow process-exec*)");
            sb.AppendLine("(allow process-fork)");
            sb.AppendLine("(allow sysctl-read)");
            sb.AppendLine("(allow mach-lookup)");
            sb.AppendLine("(allow signal (target self))");

            // System read baseline.
            foreach (string path in SystemReadPaths)
            {
                sb.AppendLine($"(allow file-read* (subpath {Quote(path)}))");
            }

            // Plan filesystem rules. ro → read; rw → read + write.
            foreach (ResolvedFsRule rule in plan.Rules)
            {
                sb.AppendLine($"(allow file-read* (subpath {Quote(rule.Path)}))");
                if (rule.CanWrite)
                {
                    sb.AppendLine($"(allow file-write* (subpath {Quote(rule.Path)}))");
                }
            }

            // Network: denied by default; allowed back only when the plan grants it.
            if (!plan.RestrictNetwork)
            {
                sb.AppendLine("(allow network*)");
            }

            return sb.ToString();
        }

        // Seatbelt string literal: escape backslash and double-quote so a path can
        // never break out of the literal (defense in depth — tokens are curated).
        private static string Quote(string path)
        {
            string escaped = path.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return "\"" + escaped + "\"";
        }
    }
}
