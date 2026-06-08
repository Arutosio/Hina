using System.Collections.Generic;
using System.Linq;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Registry;
using Hina.PackageManager.Sandbox;

namespace Hina.PackageManager.Tests
{
    public class SandboxPlannerTests
    {
        private static readonly SandboxEnv Env = new SandboxEnv(
            home: "/home/u",
            documents: "/home/u/Documents",
            download: "/home/u/Downloads",
            config: "/home/u/.config",
            tmp: "/tmp");

        private const string AppDir = "/apps/demo";

        [Fact]
        public void Resolve_App_IsInstallDir()
        {
            Assert.Equal(AppDir, SandboxPaths.Resolve(SandboxTokens.App, AppDir, Env));
        }

        [Fact]
        public void Resolve_XdgDocuments_UsesEnv()
        {
            Assert.Equal("/home/u/Documents", SandboxPaths.Resolve(SandboxTokens.XdgDocuments, AppDir, Env));
        }

        [Fact]
        public void Resolve_Host_IsNull()
        {
            Assert.Null(SandboxPaths.Resolve(SandboxTokens.Host, AppDir, Env));
        }

        [Fact]
        public void Build_AlwaysIncludesAppDirReadOnly()
        {
            SandboxSpec spec = new SandboxSpec { Enabled = true };
            SandboxPlan plan = SandboxPlanner.Build(spec, new List<FsGrant>(), AppDir, Env);

            Assert.False(plan.Unrestricted);
            ResolvedFsRule appRule = plan.Rules.Single(r => r.Path == AppDir);
            Assert.False(appRule.CanWrite);
        }

        [Fact]
        public void Build_HostRule_MarksUnrestricted()
        {
            SandboxSpec spec = new SandboxSpec
            {
                Enabled = true,
                Filesystem = new List<FsRule> { new FsRule { Path = SandboxTokens.Host, Access = "rw" } },
            };
            SandboxPlan plan = SandboxPlanner.Build(spec, new List<FsGrant>(), AppDir, Env);
            Assert.True(plan.Unrestricted);
        }

        [Fact]
        public void Build_RwRule_IsWritable()
        {
            SandboxSpec spec = new SandboxSpec
            {
                Enabled = true,
                Filesystem = new List<FsRule> { new FsRule { Path = SandboxTokens.XdgDocuments, Access = "rw" } },
            };
            SandboxPlan plan = SandboxPlanner.Build(spec, new List<FsGrant>(), AppDir, Env);
            ResolvedFsRule docs = plan.Rules.Single(r => r.Path == "/home/u/Documents");
            Assert.True(docs.CanWrite);
        }

        [Fact]
        public void Build_UserGrants_AreIncluded()
        {
            SandboxSpec spec = new SandboxSpec { Enabled = true };
            List<FsGrant> grants = new List<FsGrant> { new FsGrant { Path = "/home/u/Music", Access = "ro" } };
            SandboxPlan plan = SandboxPlanner.Build(spec, grants, AppDir, Env);
            Assert.Contains(plan.Rules, r => r.Path == "/home/u/Music" && !r.CanWrite);
        }

        [Fact]
        public void Build_NoCapabilities_RestrictsNetwork()
        {
            // A sandbox that declares no capability block grants no network — the
            // secure default is to deny it (enforced on Landlock ABI >= 4).
            SandboxSpec spec = new SandboxSpec { Enabled = true };
            SandboxPlan plan = SandboxPlanner.Build(spec, new List<FsGrant>(), AppDir, Env);
            Assert.True(plan.RestrictNetwork);
        }

        [Fact]
        public void Build_NetworkCapabilityFalse_RestrictsNetwork()
        {
            SandboxSpec spec = new SandboxSpec
            {
                Enabled = true,
                Capabilities = new CapabilitySpec { Network = false },
            };
            SandboxPlan plan = SandboxPlanner.Build(spec, new List<FsGrant>(), AppDir, Env);
            Assert.True(plan.RestrictNetwork);
        }

        [Fact]
        public void Build_NetworkCapabilityTrue_AllowsNetwork()
        {
            SandboxSpec spec = new SandboxSpec
            {
                Enabled = true,
                Capabilities = new CapabilitySpec { Network = true },
            };
            SandboxPlan plan = SandboxPlanner.Build(spec, new List<FsGrant>(), AppDir, Env);
            Assert.False(plan.RestrictNetwork);
        }

        [Fact]
        public void Build_HostRule_DoesNotRestrictNetwork()
        {
            // host = unrestricted filesystem; there is nothing to scope, and the
            // app explicitly opted out of isolation — don't restrict its network.
            SandboxSpec spec = new SandboxSpec
            {
                Enabled = true,
                Capabilities = new CapabilitySpec { Network = false },
                Filesystem = new List<FsRule> { new FsRule { Path = SandboxTokens.Host, Access = "rw" } },
            };
            SandboxPlan plan = SandboxPlanner.Build(spec, new List<FsGrant>(), AppDir, Env);
            Assert.False(plan.RestrictNetwork);
        }

        [Fact]
        public void Build_DuplicatePath_RwWins()
        {
            SandboxSpec spec = new SandboxSpec
            {
                Enabled = true,
                Filesystem = new List<FsRule> { new FsRule { Path = SandboxTokens.XdgConfig, Access = "ro" } },
            };
            List<FsGrant> grants = new List<FsGrant> { new FsGrant { Path = "/home/u/.config", Access = "rw" } };
            SandboxPlan plan = SandboxPlanner.Build(spec, grants, AppDir, Env);
            ResolvedFsRule cfg = plan.Rules.Single(r => r.Path == "/home/u/.config");
            Assert.True(cfg.CanWrite);
        }
    }
}
