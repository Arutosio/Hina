using System.Linq;
using Hina.Builder.Init;
using Hina.PackageManager.Descriptor;

namespace Hina.Builder.Tests
{
    public sealed class SandboxAnswersTests
    {
        [Fact]
        public void Disabled_ReturnsNull()
        {
            Assert.Null(new SandboxAnswers { Enabled = false }.ToSpec());
        }

        [Fact]
        public void SelfOnly_NoNetwork_HasNoRulesOrCaps()
        {
            SandboxSpec? spec = new SandboxAnswers
            {
                Enabled = true,
                NeedsNetwork = false,
                DataLocation = DataLocation.SelfOnly
            }.ToSpec();

            Assert.NotNull(spec);
            Assert.True(spec!.Enabled);
            Assert.Empty(spec.Filesystem);
            Assert.Null(spec.Capabilities);
        }

        [Fact]
        public void ConfigWithNetwork_AddsTokenAndCapability()
        {
            SandboxSpec? spec = new SandboxAnswers
            {
                Enabled = true,
                NeedsNetwork = true,
                DataLocation = DataLocation.Config
            }.ToSpec();

            Assert.NotNull(spec);
            FsRule rule = Assert.Single(spec!.Filesystem);
            Assert.Equal(SandboxTokens.XdgConfig, rule.Path);
            Assert.Equal("rw", rule.Access);
            Assert.True(spec.Capabilities!.Network);
        }

        [Fact]
        public void Anywhere_MapsToHostToken()
        {
            SandboxSpec? spec = new SandboxAnswers
            {
                Enabled = true,
                DataLocation = DataLocation.Anywhere
            }.ToSpec();

            Assert.Equal(SandboxTokens.Host, spec!.Filesystem.Single().Path);
        }
    }
}
