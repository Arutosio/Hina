using System.Collections.Generic;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Registry;
using Hina.PackageManager.Sandbox;

namespace Hina.PackageManager.Tests
{
    public class PermissionsFormatterTests
    {
        private static AppPermissions Sample() => new AppPermissions
        {
            Name = "signal",
            SandboxEnabled = true,
            FilesystemDeclared = new List<FsRule>
            {
                new FsRule { Path = "home", Access = "ro" },
                new FsRule { Path = "xdg-documents", Access = "rw" },
            },
            UserGrants = new List<FsGrant> { new FsGrant { Path = "/home/u/Music", Access = "rw" } },
            Network = true,
            Microphone = true,
        };

        [Fact]
        public void Table_HasHeaderAndLegend()
        {
            string t = PermissionsFormatter.Table(new[] { Sample() });
            Assert.Contains("APP", t);
            Assert.Contains("NET", t);
            Assert.Contains("DEV", t);
            // Legend must say filesystem AND network are enforced now (no longer
            // "only filesystem"), and that the remaining caps are not enforced.
            string lower = t.ToLowerInvariant();
            Assert.DoesNotContain("only filesystem", lower);
            Assert.Contains("network", lower);
            Assert.Contains("enforced", lower);
        }

        [Fact]
        public void Detail_NetworkDenied_ShownAsEnforced()
        {
            // A sandboxed app that did not declare network has it DENIED — and that
            // denial is enforced (Linux 6.7+/macOS), not merely declared.
            AppPermissions p = new AppPermissions { Name = "boxed", SandboxEnabled = true, Network = false };
            string d = PermissionsFormatter.Detail(p).ToLowerInvariant();
            Assert.Contains("network", d);
            Assert.Contains("denied", d);
            Assert.DoesNotContain("network:      declared (not enforced)", d);
        }

        [Fact]
        public void Table_RowShowsDeclaredMarks()
        {
            string t = PermissionsFormatter.Table(new[] { Sample() });
            Assert.Contains("signal", t);
            // declared capability marker present, undeclared shown as dash
            Assert.Contains("✓", t);
            Assert.Contains("—", t);
        }

        [Fact]
        public void Table_HostScopeFlaggedLoudly()
        {
            AppPermissions p = new AppPermissions
            {
                Name = "gimp",
                SandboxEnabled = true,
                FilesystemDeclared = new List<FsRule> { new FsRule { Path = "host", Access = "rw" } },
            };
            string t = PermissionsFormatter.Table(new[] { p });
            Assert.Contains("host(!)", t);
        }

        [Fact]
        public void Table_UnsandboxedAppRowRendered()
        {
            string t = PermissionsFormatter.Table(new[]
            {
                new AppPermissions { Name = "oldapp", SandboxEnabled = false },
            });
            Assert.Contains("oldapp", t);
            Assert.Contains("no", t);
        }

        [Fact]
        public void Detail_ListsEnforcedFilesystemAndGrants()
        {
            string d = PermissionsFormatter.Detail(Sample());
            Assert.Contains("signal", d);
            Assert.Contains("home", d);
            Assert.Contains("/home/u/Music", d);
            Assert.Contains("user grant", d.ToLowerInvariant());
            Assert.Contains("enforced", d.ToLowerInvariant());
        }

        [Fact]
        public void Detail_CapabilitiesShowNotEnforcedCaveat()
        {
            string d = PermissionsFormatter.Detail(Sample());
            Assert.Contains("Network", d);
            Assert.Contains("Microphone", d);
            // declared caps carry the not-enforced marker
            Assert.Contains("not enforced", d.ToLowerInvariant());
        }

        [Fact]
        public void CapabilityDisclosure_NetworkDeniedAndDeclaredExtras()
        {
            // network not declared -> denied & enforced; audio declared -> not enforced.
            CapabilitySpec caps = new CapabilitySpec { Network = false, Audio = true };
            string text = string.Join("\n", PermissionsFormatter.CapabilityDisclosure(caps)).ToLowerInvariant();
            Assert.Contains("network", text);
            Assert.Contains("denied", text);
            Assert.Contains("audio", text);
            Assert.Contains("not enforced", text);
        }

        [Fact]
        public void CapabilityDisclosure_NetworkAllowed_OmitsUndeclaredCaps()
        {
            CapabilitySpec caps = new CapabilitySpec { Network = true };
            var lines = PermissionsFormatter.CapabilityDisclosure(caps);
            string text = string.Join("\n", lines).ToLowerInvariant();
            Assert.Contains("network", text);
            Assert.Contains("allowed", text);
            // Undeclared caps are not listed (keep disclosure terse).
            Assert.DoesNotContain("microphone", text);
        }

        [Fact]
        public void Compact_SandboxedApp_ShowsScopeAndNetwork()
        {
            string c = PermissionsFormatter.Compact(Sample()).ToLowerInvariant();
            Assert.Contains("sandbox", c);
            Assert.Contains("network", c);
        }

        [Fact]
        public void Compact_UnsandboxedApp_SaysNoIsolation()
        {
            string c = PermissionsFormatter.Compact(new AppPermissions { Name = "oldapp", SandboxEnabled = false }).ToLowerInvariant();
            Assert.Contains("no isolation", c);
        }

        [Fact]
        public void Detail_DisabledSandboxWarnsNoIsolation()
        {
            string d = PermissionsFormatter.Detail(new AppPermissions { Name = "oldapp", SandboxEnabled = false });
            Assert.Contains("oldapp", d);
            Assert.Contains("no isolation", d.ToLowerInvariant());
        }
    }
}
