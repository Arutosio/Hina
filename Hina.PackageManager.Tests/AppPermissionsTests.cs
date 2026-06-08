using System.Collections.Generic;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Registry;
using Hina.PackageManager.Sandbox;

namespace Hina.PackageManager.Tests
{
    public class AppPermissionsTests
    {
        private static AppDescriptor Desc(SandboxSpec? sandbox) => new AppDescriptor
        {
            Name = "demo",
            Sandbox = sandbox,
        };

        [Fact]
        public void From_NoSandbox_AllFalseEmpty()
        {
            AppPermissions p = AppPermissions.From(Desc(null), new InstalledApp { Name = "demo" });

            Assert.Equal("demo", p.Name);
            Assert.False(p.SandboxEnabled);
            Assert.Empty(p.FilesystemDeclared);
            Assert.Empty(p.UserGrants);
            Assert.False(p.Network);
            Assert.False(p.Devices);
        }

        [Fact]
        public void From_MapsFilesystemAndGrants()
        {
            SandboxSpec s = new SandboxSpec
            {
                Enabled = true,
                Filesystem = new List<FsRule> { new FsRule { Path = "home", Access = "ro" } },
            };
            InstalledApp app = new InstalledApp
            {
                Name = "demo",
                UserGrants = new List<FsGrant> { new FsGrant { Path = "/home/u/Music", Access = "rw" } },
            };

            AppPermissions p = AppPermissions.From(Desc(s), app);

            Assert.True(p.SandboxEnabled);
            Assert.Single(p.FilesystemDeclared);
            Assert.Equal("home", p.FilesystemDeclared[0].Path);
            Assert.Single(p.UserGrants);
            Assert.Equal("/home/u/Music", p.UserGrants[0].Path);
        }

        [Fact]
        public void From_MapsCapabilities()
        {
            SandboxSpec s = new SandboxSpec
            {
                Enabled = true,
                Capabilities = new CapabilitySpec { Network = true, Microphone = true, Devices = true },
            };

            AppPermissions p = AppPermissions.From(Desc(s), new InstalledApp { Name = "demo" });

            Assert.True(p.Network);
            Assert.True(p.Microphone);
            Assert.True(p.Devices);
            Assert.False(p.Audio);
            Assert.False(p.Screen);
            Assert.False(p.Input);
        }
    }
}
