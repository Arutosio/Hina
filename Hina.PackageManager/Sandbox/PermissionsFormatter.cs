using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Hina.PackageManager.Descriptor;

namespace Hina.PackageManager.Sandbox
{
    // Renders AppPermissions as the `hina perms` table (all apps) and detail
    // (one app). Pure string output. Filesystem and network are enforced; the other
    // capabilities are rendered with a "not enforced" caveat so the display never
    // implies isolation Hina does not provide.
    public static class PermissionsFormatter
    {
        private const string Yes = "✓";
        private const string No = "—";

        private static readonly string[] Headers = { "APP", "SANDBOX", "FS", "NET", "AUDIO", "MIC", "SCREEN", "INPUT", "DEV" };

        public static string Table(IEnumerable<AppPermissions> apps)
        {
            List<string[]> rows = new List<string[]> { Headers };
            foreach (AppPermissions p in apps)
            {
                rows.Add(new[]
                {
                    p.Name,
                    p.SandboxEnabled ? "yes" : "no",
                    FsSummary(p),
                    Mark(p.Network),
                    Mark(p.Audio),
                    Mark(p.Microphone),
                    Mark(p.Screen),
                    Mark(p.Input),
                    Mark(p.Devices),
                });
            }

            int cols = Headers.Length;
            int[] widths = new int[cols];
            foreach (string[] row in rows)
            {
                for (int c = 0; c < cols; c++)
                {
                    widths[c] = Math.Max(widths[c], DisplayWidth(row[c]));
                }
            }

            StringBuilder sb = new StringBuilder();
            foreach (string[] row in rows)
            {
                for (int c = 0; c < cols; c++)
                {
                    sb.Append(Pad(row[c], widths[c]));
                    if (c < cols - 1) sb.Append("  ");
                }
                sb.Append('\n');
            }

            sb.Append('\n');
            sb.Append("Filesystem (FS) and network (NET) are enforced — Linux/Landlock + macOS/sandbox-exec\n");
            sb.Append("(NET needs Linux 6.7+). AUDIO/MIC/SCREEN/INPUT/DEV are declared but NOT enforced.\n");
            sb.Append("Run `hina perms <app>` for details.\n");
            return sb.ToString();
        }

        public static string Detail(AppPermissions p)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(p.Name).Append('\n');

            if (!p.SandboxEnabled)
            {
                sb.Append("  Sandbox: disabled — app runs with full user privileges (no isolation).\n");
                return sb.ToString();
            }

            sb.Append("  Sandbox: enabled\n");
            sb.Append("  Filesystem (enforced):\n");
            sb.Append("    ro   <install dir>   (app itself)\n");
            foreach (FsRule rule in p.FilesystemDeclared)
            {
                string label = rule.Path == SandboxTokens.Host ? "host — UNRESTRICTED, no isolation" : rule.Path;
                sb.Append("    ").Append(PadRight(rule.Access, 4)).Append(' ').Append(label).Append("   (declared)\n");
            }
            foreach (var grant in p.UserGrants)
            {
                sb.Append("    ").Append(PadRight(grant.Access, 4)).Append(' ').Append(grant.Path).Append("   (user grant)\n");
            }
            if (p.FilesystemDeclared.Count == 0 && p.UserGrants.Count == 0)
            {
                sb.Append("    (nothing beyond the install dir)\n");
            }

            // Network is enforced (Linux 6.7+/macOS), so it reads differently from the
            // declared-only capabilities: granted vs actively denied, both enforced.
            sb.Append("  ").Append(PadRight("Network:", 12)).Append(' ');
            sb.Append(p.Network
                ? "allowed (enforced)\n"
                : "denied (enforced on Linux 6.7+/macOS)\n");

            Capability(sb, "Audio", p.Audio);
            Capability(sb, "Microphone", p.Microphone);
            Capability(sb, "Screen", p.Screen);
            Capability(sb, "Input", p.Input);
            Capability(sb, "Devices", p.Devices);
            return sb.ToString();
        }

        // Human-readable capability lines for install-time disclosure. Network is
        // always shown (enforced); the other capabilities are listed only when the
        // app declares them, each marked NOT enforced so the user is never misled.
        public static IReadOnlyList<string> CapabilityDisclosure(CapabilitySpec? caps)
        {
            CapabilitySpec c = caps ?? new CapabilitySpec();
            List<string> lines = new List<string>
            {
                c.Network
                    ? "network: ALLOWED (declared by the app)"
                    : "network: denied (enforced on Linux 6.7+/macOS)",
            };
            foreach ((string name, bool on) in DeclaredExtras(c))
            {
                if (on) lines.Add($"{name}: declared — NOT enforced (no portal/policy yet)");
            }
            return lines;
        }

        // A compact one-block permission summary for `hina info`.
        public static string Compact(AppPermissions p)
        {
            StringBuilder sb = new StringBuilder();
            if (!p.SandboxEnabled)
            {
                sb.Append("Sandbox:        disabled — full user privileges (no isolation)\n");
                return sb.ToString();
            }
            sb.Append("Sandbox:        enabled\n");
            sb.Append("  Filesystem:   ").Append(FsSummary(p)).Append("  (enforced: Linux/macOS)\n");
            sb.Append("  Network:      ").Append(p.Network ? "allowed (enforced)" : "denied (enforced)").Append('\n');
            List<string> extras = new List<string>();
            foreach ((string name, bool on) in DeclaredExtras(new CapabilitySpec
            {
                Audio = p.Audio, Microphone = p.Microphone, Screen = p.Screen, Input = p.Input, Devices = p.Devices,
            }))
            {
                if (on) extras.Add(name.ToLowerInvariant());
            }
            if (extras.Count > 0)
            {
                sb.Append("  Declared:     ").Append(string.Join(", ", extras)).Append("  (NOT enforced)\n");
            }
            return sb.ToString();
        }

        private static IEnumerable<(string, bool)> DeclaredExtras(CapabilitySpec c)
        {
            yield return ("Audio", c.Audio);
            yield return ("Microphone", c.Microphone);
            yield return ("Screen", c.Screen);
            yield return ("Input", c.Input);
            yield return ("Devices", c.Devices);
        }

        private static void Capability(StringBuilder sb, string name, bool declared)
        {
            sb.Append("  ").Append(PadRight(name + ":", 12)).Append(' ');
            sb.Append(declared ? "declared (not enforced)" : "not declared").Append('\n');
        }

        private static string Mark(bool b) => b ? Yes : No;

        private static string FsSummary(AppPermissions p)
        {
            if (!p.SandboxEnabled) return No;

            List<string> tokens = p.FilesystemDeclared.Select(r => r.Path).ToList();
            if (tokens.Contains(SandboxTokens.Host))
            {
                return "host(!)";
            }

            string baseScope = tokens.Count > 0 ? string.Join(",", tokens) : "";
            if (p.UserGrants.Count > 0)
            {
                baseScope = baseScope.Length > 0 ? $"{baseScope}+{p.UserGrants.Count}g" : $"+{p.UserGrants.Count}g";
            }
            return baseScope.Length > 0 ? baseScope : No;
        }

        // ✓ and — are single display columns but multi-byte in UTF-8; measure by
        // text-element count, not byte length, so padding lines up.
        private static int DisplayWidth(string s) => new System.Globalization.StringInfo(s).LengthInTextElements;

        private static string Pad(string s, int width)
            => s + new string(' ', Math.Max(0, width - DisplayWidth(s)));

        private static string PadRight(string s, int width) => Pad(s, width);
    }
}
