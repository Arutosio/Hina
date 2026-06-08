using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Hina.PackageManager.Sandbox
{
    // macOS filesystem (and network) sandbox backend. Generates a Seatbelt profile
    // from the plan (see MacOsSeatbeltProfile) and launches the app under
    // `sandbox-exec -f <profile>`.
    //
    // sandbox-exec is deprecated by Apple but still present and functional on every
    // supported macOS; it is far simpler than the private libsandbox API and needs
    // no entitlements. We spawn it as a child (rather than execve) so we can delete
    // the temporary profile file once the app exits. Fail-soft: any setup failure
    // logs a warning and runs the app unsandboxed — a sandbox problem never blocks a
    // launch.
    public sealed class MacOsSandbox : ISandboxLauncher
    {
        private const string SandboxExec = "/usr/bin/sandbox-exec";

        private readonly ILogger _logger;

        public MacOsSandbox(ILogger logger)
        {
            _logger = logger;
        }

        public bool IsSupported => OperatingSystem.IsMacOS() && File.Exists(SandboxExec);

        public int Launch(string execAbs, IReadOnlyList<string> appArgs, SandboxPlan plan, CancellationToken ct)
        {
            if (plan.Unrestricted || !IsSupported)
            {
                // Nothing to enforce (host opt-out) or no sandbox-exec — run directly.
                return Spawn(execAbs, appArgs, profile: null, ct);
            }

            string? profilePath = null;
            try
            {
                // Seatbelt matches CANONICAL paths, but macOS scatters symlinks across
                // the standard tree (/tmp, /var, /etc → /private/...). Resolve each
                // granted path to its real location so the rule actually matches, or a
                // write to /tmp/... (really /private/tmp/...) would be silently denied.
                SandboxPlan resolved = Canonicalize(plan);
                profilePath = Path.Combine(Path.GetTempPath(), "hina-sandbox-" + Guid.NewGuid().ToString("N") + ".sb");
                File.WriteAllText(profilePath, MacOsSeatbeltProfile.Build(resolved));
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not write sandbox profile ({Message}); running unsandboxed.", ex.Message);
                return Spawn(execAbs, appArgs, profile: null, ct);
            }

            try
            {
                return Spawn(execAbs, appArgs, profilePath, ct);
            }
            finally
            {
                try { File.Delete(profilePath); }
                catch (Exception ex) { _logger.LogDebug(ex, "Best-effort: could not delete sandbox profile {Path}", profilePath); }
            }
        }

        // Resolve every rule path to its canonical (symlink-free) location so Seatbelt
        // subpath rules match. A path that doesn't resolve (missing / permission) is
        // kept verbatim — the grant simply won't match anything, which is fail-safe.
        private static SandboxPlan Canonicalize(SandboxPlan plan)
        {
            List<ResolvedFsRule> resolved = new List<ResolvedFsRule>(plan.Rules.Count);
            foreach (ResolvedFsRule rule in plan.Rules)
            {
                resolved.Add(new ResolvedFsRule(RealPath(rule.Path), rule.CanWrite));
            }
            return new SandboxPlan(plan.Unrestricted, resolved, plan.RestrictNetwork);
        }

        private static string RealPath(string path)
        {
            IntPtr p = realpath(path, IntPtr.Zero);
            if (p == IntPtr.Zero)
            {
                return path;
            }
            try { return Marshal.PtrToStringUTF8(p) ?? path; }
            finally { free(p); }
        }

        [DllImport("libc", SetLastError = true)]
        private static extern IntPtr realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolved);

        [DllImport("libc")]
        private static extern void free(IntPtr ptr);

        private int Spawn(string execAbs, IReadOnlyList<string> appArgs, string? profile, CancellationToken ct)
        {
            ProcessStartInfo psi = new ProcessStartInfo { UseShellExecute = false };
            if (profile != null)
            {
                psi.FileName = SandboxExec;
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add(profile);
                psi.ArgumentList.Add(execAbs);
            }
            else
            {
                psi.FileName = execAbs;
            }
            foreach (string a in appArgs)
            {
                psi.ArgumentList.Add(a);
            }

            using Process proc = Process.Start(psi)!;
            // Forward a cancellation to the child so the sandbox launch honours ct.
            using CancellationTokenRegistration reg = ct.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
                catch { /* race: already gone */ }
            });
            proc.WaitForExit();
            return proc.ExitCode;
        }
    }
}
