using System.Collections.Generic;
using Hina.PackageManager.Sandbox;
using Xunit;

namespace Hina.PackageManager.Tests
{
    // The Seatbelt (.sb) profile is the testable, pure core of the macOS sandbox
    // backend. sandbox-exec enforcement itself is exercised by an integration test
    // / CI probe; here we lock down the generated policy text.
    public class MacOsSeatbeltProfileTests
    {
        private static SandboxPlan Plan(bool restrictNetwork, params ResolvedFsRule[] rules)
            => new SandboxPlan(unrestricted: false, rules, restrictNetwork);

        [Fact]
        public void StartsWithVersionAndDeniesByDefault()
        {
            string p = MacOsSeatbeltProfile.Build(Plan(false, new ResolvedFsRule("/apps/demo", false)));
            Assert.StartsWith("(version 1)", p);
            Assert.Contains("(deny default)", p);
        }

        [Fact]
        public void AllowsProcessExec()
        {
            string p = MacOsSeatbeltProfile.Build(Plan(false, new ResolvedFsRule("/apps/demo", false)));
            Assert.Contains("(allow process-exec", p);
            Assert.Contains("(allow process-fork)", p);
        }

        [Fact]
        public void ReadOnlyRule_GrantsReadNotWrite()
        {
            string p = MacOsSeatbeltProfile.Build(Plan(false, new ResolvedFsRule("/data/docs", false)));
            Assert.Contains("(allow file-read* (subpath \"/data/docs\"))", p);
            Assert.DoesNotContain("(allow file-write* (subpath \"/data/docs\"))", p);
        }

        [Fact]
        public void ReadWriteRule_GrantsReadAndWrite()
        {
            string p = MacOsSeatbeltProfile.Build(Plan(false, new ResolvedFsRule("/data/docs", true)));
            Assert.Contains("(allow file-read* (subpath \"/data/docs\"))", p);
            Assert.Contains("(allow file-write* (subpath \"/data/docs\"))", p);
        }

        [Fact]
        public void IncludesSystemBaselineReads()
        {
            // A GUI/CLI app must read the dyld cache + system frameworks to start.
            string p = MacOsSeatbeltProfile.Build(Plan(false, new ResolvedFsRule("/apps/demo", false)));
            Assert.Contains("(subpath \"/usr/lib\")", p);
            Assert.Contains("(subpath \"/System\")", p);
        }

        [Fact]
        public void NetworkRestricted_OmitsNetworkAllow()
        {
            string p = MacOsSeatbeltProfile.Build(Plan(restrictNetwork: true, new ResolvedFsRule("/apps/demo", false)));
            Assert.DoesNotContain("(allow network", p);
        }

        [Fact]
        public void NetworkAllowed_GrantsNetwork()
        {
            string p = MacOsSeatbeltProfile.Build(Plan(restrictNetwork: false, new ResolvedFsRule("/apps/demo", false)));
            Assert.Contains("(allow network*)", p);
        }

        [Fact]
        public void EscapesQuotesInPaths()
        {
            // A path containing a double quote must not break out of the string literal.
            string p = MacOsSeatbeltProfile.Build(Plan(false, new ResolvedFsRule("/weird/\"x\"", false)));
            Assert.Contains("\\\"x\\\"", p);
        }
    }
}
