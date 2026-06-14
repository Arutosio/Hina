using System;
using System.Collections.Generic;
using Hina.PackageManager.Descriptor;

namespace Hina.PackageManager.Sandbox
{
    // Compares the sandbox an app declared in its OLD (cached) descriptor against the
    // NEW one fetched during an update, and decides whether the new version BROADENS
    // the app's access. Broadening requires user consent (`hina update --accept-new-
    // permissions`); narrowing applies silently.
    //
    // Baseline model: a null/disabled sandbox means "unsandboxed = full access". So
    // enabling/tightening a sandbox can only narrow; dropping or loosening one broadens.
    public sealed class SandboxDiff
    {
        public bool Broadened => Added.Count > 0;
        public List<string> Added { get; } = new List<string>();
        public List<string> Removed { get; } = new List<string>();

        public static SandboxDiff Compute(SandboxSpec? oldSpec, SandboxSpec? newSpec)
        {
            SandboxDiff diff = new SandboxDiff();

            bool oldOn = oldSpec?.Enabled == true;
            bool newOn = newSpec?.Enabled == true;

            // Unsandboxed (full access) on both sides — nothing changes.
            if (!oldOn && !newOn) return diff;

            // Was restricted, now unrestricted → the app regains full filesystem access.
            if (oldOn && !newOn)
            {
                diff.Added.Add("host (sandbox removed — full filesystem access)");
                return diff;
            }

            // Was unrestricted, now restricted → strictly narrowing.
            if (!oldOn && newOn)
            {
                diff.Removed.Add("(app is now sandboxed)");
                return diff;
            }

            // Both restricted — compare declared scope.
            Dictionary<string, string> oldFs = ToMap(oldSpec!);
            Dictionary<string, string> newFs = ToMap(newSpec!);

            bool oldHasHost = oldFs.ContainsKey(SandboxTokens.Host);
            bool newHasHost = newFs.ContainsKey(SandboxTokens.Host);

            // 'host' means no filesystem restriction — it is the maximal set.
            // old=host → new=specific paths : strictly narrowing (not a broadening).
            // old=specific paths → new=host  : strictly broadening.
            if (oldHasHost && !newHasHost)
            {
                // Every specific path in the new spec is a restriction compared to host.
                foreach (KeyValuePair<string, string> kv in newFs)
                    diff.Removed.Add($"{kv.Key} ({kv.Value})");
                diff.Removed.Add("host (replaced by restricted path list)");
                // Skip the per-key comparison below; capabilities still need checking.
                CompareCapabilities(oldSpec!.Capabilities, newSpec!.Capabilities, diff);
                return diff;
            }

            if (!oldHasHost && newHasHost)
            {
                // Gaining host means the app now requests unrestricted filesystem access.
                diff.Added.Add($"{SandboxTokens.Host} (unrestricted filesystem access)");
                // Any previously granted specific paths are superseded — list them as removed
                // for completeness, but the broadening is driven by host being added.
                foreach (KeyValuePair<string, string> kv in oldFs)
                    diff.Removed.Add($"{kv.Key} ({kv.Value}) (superseded by host)");
                CompareCapabilities(oldSpec!.Capabilities, newSpec!.Capabilities, diff);
                return diff;
            }

            // Neither side uses 'host' (or both do — treated as no change on the host axis).
            foreach (KeyValuePair<string, string> kv in newFs)
            {
                if (!oldFs.TryGetValue(kv.Key, out string? oldAccess))
                {
                    diff.Added.Add($"{kv.Key} ({kv.Value})");
                }
                else if (oldAccess == "ro" && kv.Value == "rw")
                {
                    diff.Added.Add($"{kv.Key} (ro → rw)");
                }
            }
            foreach (KeyValuePair<string, string> kv in oldFs)
            {
                if (!newFs.ContainsKey(kv.Key))
                {
                    diff.Removed.Add($"{kv.Key} ({kv.Value})");
                }
                else if (kv.Value == "rw" && newFs[kv.Key] == "ro")
                {
                    diff.Removed.Add($"{kv.Key} (rw → ro)");
                }
            }

            CompareCapabilities(oldSpec!.Capabilities, newSpec!.Capabilities, diff);
            return diff;
        }

        // path token -> highest access (rw wins over ro if a token were listed twice).
        private static Dictionary<string, string> ToMap(SandboxSpec spec)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (FsRule r in spec.Filesystem)
            {
                if (map.TryGetValue(r.Path, out string? existing) && existing == "rw") continue;
                map[r.Path] = r.Access;
            }
            return map;
        }

        private static void CompareCapabilities(CapabilitySpec? oldCaps, CapabilitySpec? newCaps, SandboxDiff diff)
        {
            oldCaps ??= new CapabilitySpec();
            newCaps ??= new CapabilitySpec();
            foreach ((string name, bool o, bool n) in new[]
            {
                ("network", oldCaps.Network, newCaps.Network),
                ("audio", oldCaps.Audio, newCaps.Audio),
                ("microphone", oldCaps.Microphone, newCaps.Microphone),
                ("screen", oldCaps.Screen, newCaps.Screen),
                ("input", oldCaps.Input, newCaps.Input),
                ("devices", oldCaps.Devices, newCaps.Devices),
            })
            {
                if (!o && n) diff.Added.Add($"capability: {name}");
                else if (o && !n) diff.Removed.Add($"capability: {name}");
            }
        }
    }
}
