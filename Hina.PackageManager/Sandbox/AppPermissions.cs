using System.Collections.Generic;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Registry;

namespace Hina.PackageManager.Sandbox
{
    // A flat, display-ready view of one app's permissions, folded from its cached
    // descriptor (declared scope + capabilities) and registry row (user grants).
    // Pure mapping — no I/O — so the CLI formatters stay trivial and testable.
    public sealed class AppPermissions
    {
        public string Name { get; init; } = string.Empty;
        public bool SandboxEnabled { get; init; }

        // Filesystem is the only ENFORCED category (Linux/Landlock).
        public IReadOnlyList<FsRule> FilesystemDeclared { get; init; } = new List<FsRule>();
        public IReadOnlyList<FsGrant> UserGrants { get; init; } = new List<FsGrant>();

        // Declared-only, not enforced in v1.
        public bool Network { get; init; }
        public bool Audio { get; init; }
        public bool Microphone { get; init; }
        public bool Screen { get; init; }
        public bool Input { get; init; }
        public bool Devices { get; init; }

        public static AppPermissions From(AppDescriptor descriptor, InstalledApp app)
        {
            SandboxSpec? sandbox = descriptor.Sandbox;
            CapabilitySpec caps = sandbox?.Capabilities ?? new CapabilitySpec();

            return new AppPermissions
            {
                Name = app.Name,
                SandboxEnabled = sandbox?.Enabled == true,
                FilesystemDeclared = sandbox?.Filesystem ?? new List<FsRule>(),
                UserGrants = app.UserGrants,
                Network = caps.Network,
                Audio = caps.Audio,
                Microphone = caps.Microphone,
                Screen = caps.Screen,
                Input = caps.Input,
                Devices = caps.Devices,
            };
        }
    }
}
