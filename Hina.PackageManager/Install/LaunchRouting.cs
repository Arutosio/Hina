using System.Runtime.InteropServices;
using Hina.PackageManager.Descriptor;

namespace Hina.PackageManager.Install
{
    // Builds the launchOverride that routes a shell entry through `hina run`, the chokepoint
    // that installs the per-app sandbox (Landlock/AppContainer/seatbelt) before the app
    // process starts. Shared by InstallService and UpdateService so the two stay in lockstep:
    // a divergence (update created shortcuts without the override) was BUG-043, letting
    // update-added entries of a sandboxed app launch unsandboxed.
    internal static class LaunchRouting
    {
        // Returns the `hina run` override string, or null when the entry must launch the app
        // binary directly: either the descriptor does not request a sandbox, or the current OS
        // has no enforceable backend (where routing through `hina run` would gain nothing).
        public static string? BuildLaunchOverride(AppDescriptor descriptor, ShellEntry entry)
        {
            bool sandboxRequested = descriptor.Sandbox?.Enabled == true;
            bool sandboxEnforceable = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                || RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            if (!(sandboxRequested && sandboxEnforceable))
            {
                return null;
            }
            string hinaExe = Environment.ProcessPath ?? "hina";
            return $"\"{hinaExe}\" run {descriptor.Name} \"{entry.Id}\"";
        }
    }
}
