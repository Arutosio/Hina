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
            // Legend must state only filesystem is enforced.
            Assert.Contains("only filesystem", t.ToLowerInvariant());
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
        public void Detail_DisabledSandboxWarnsNoIsolation()
        {
            string d = PermissionsFormatter.Detail(new AppPermissions { Name = "oldapp", SandboxEnabled = false });
            Assert.Contains("oldapp", d);
            Assert.Contains("no isolation", d.ToLowerInvariant());
        }
    }
}
