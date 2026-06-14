using System.Collections.Generic;
using System.Linq;
using Hina.PackageManager.Sandbox;
using Xunit;

namespace Hina.PackageManager.Tests
{
    // The AppContainer POLICY (which paths get which ACEs, which capability SIDs to
    // attach, the container moniker) is the testable, pure core of the Windows sandbox
    // backend — the analogue of MacOsSeatbeltProfile. The raw CreateProcess / ACL
    // P/Invoke that consumes this policy is exercised by the windows-latest CI probe,
    // not here (it cannot run on the macOS/Linux dev host).
    public class WindowsAppContainerPolicyTests
    {
        private static SandboxPlan Plan(bool restrictNetwork, params ResolvedFsRule[] rules)
            => new SandboxPlan(unrestricted: false, rules, restrictNetwork);

        // ---- ContainerName (BUG-009 regression tests) ----

        [Fact]
        public void ContainerName_PrefixesHinaAndIsDeterministic()
        {
            // Same (basename, anchor) always produces the same moniker.
            string a = WindowsAppContainerPolicy.ContainerName("signal", @"C:\Apps\signal");
            string b = WindowsAppContainerPolicy.ContainerName("signal", @"C:\Apps\signal");
            Assert.Equal(a, b);
            Assert.StartsWith("Hina.", a);
            Assert.Contains("signal", a);
        }

        [Fact]
        public void ContainerName_SanitizesIllegalCharsAndCapsAt64()
        {
            // AppContainer monikers are limited to 64 chars and a constrained charset;
            // spaces / slashes must not leak into the name.
            string name = WindowsAppContainerPolicy.ContainerName("My App/../weird name!", @"C:\Apps\myapp");
            Assert.DoesNotContain(" ", name);
            Assert.DoesNotContain("/", name);
            Assert.DoesNotContain("\\", name);
            Assert.True(name.Length <= 64, $"name too long: {name.Length}");
        }

        [Fact]
        public void ContainerName_LongAppNameStillCapsAt64()
        {
            string name = WindowsAppContainerPolicy.ContainerName(new string('x', 200), @"C:\Apps\longapp");
            Assert.True(name.Length <= 64, $"name too long: {name.Length}");
        }

        // BUG-009: two apps with identical exe basenames but different install paths
        // must receive DIFFERENT container names (and therefore different SIDs).
        [Fact]
        public void ContainerName_SameBasenameDifferentPath_ProducesDifferentNames()
        {
            string nameA = WindowsAppContainerPolicy.ContainerName("app", @"C:\Apps\foo");
            string nameB = WindowsAppContainerPolicy.ContainerName("app", @"C:\Apps\bar");
            Assert.NotEqual(nameA, nameB);
        }

        // BUG-009: same (basename, anchor) must always produce the same name so the
        // AppContainer profile created at first launch is reused on subsequent launches.
        [Fact]
        public void ContainerName_SamePathSameBasename_IsDeterministic()
        {
            string first  = WindowsAppContainerPolicy.ContainerName("app", @"C:\Apps\myapp");
            string second = WindowsAppContainerPolicy.ContainerName("app", @"C:\Apps\myapp");
            Assert.Equal(first, second);
        }

        // BUG-009: even with a 200-char basename the hash suffix must remain intact
        // so that two different anchors still produce distinct names after the 64-char cap.
        [Fact]
        public void ContainerName_HashSurvivesLengthCap()
        {
            string longBasename = new string('x', 200);
            string nameA = WindowsAppContainerPolicy.ContainerName(longBasename, @"C:\Apps\alpha");
            string nameB = WindowsAppContainerPolicy.ContainerName(longBasename, @"C:\Apps\beta");

            Assert.True(nameA.Length <= 64, $"nameA too long: {nameA.Length}");
            Assert.True(nameB.Length <= 64, $"nameB too long: {nameB.Length}");
            // Different anchors must still be distinguishable even after truncation.
            Assert.NotEqual(nameA, nameB);
        }

        // BUG-009: minor spelling differences in the anchor that denote the same
        // directory (trailing separator, case) must not produce spurious extra SIDs.
        [Fact]
        public void ContainerName_PathCanonicalization_TrailingSlashAndCaseAreIgnored()
        {
            // Trailing directory separator stripped.
            string withSlash    = WindowsAppContainerPolicy.ContainerName("app", @"C:\Apps\myapp\");
            string withoutSlash = WindowsAppContainerPolicy.ContainerName("app", @"C:\Apps\myapp");
            Assert.Equal(withSlash, withoutSlash);

            // Lower-invariant: on Windows path comparison is case-insensitive.
            string upper = WindowsAppContainerPolicy.ContainerName("app", @"C:\Apps\MyApp");
            string lower = WindowsAppContainerPolicy.ContainerName("app", @"C:\apps\myapp");
            Assert.Equal(upper, lower);
        }

        [Fact]
        public void BuildAceList_ReadOnlyRule_GrantsReadExecuteNotWrite()
        {
            IReadOnlyList<AppContainerAce> aces =
                WindowsAppContainerPolicy.BuildAceList(Plan(false, new ResolvedFsRule(@"C:\data\docs", false)));
            AppContainerAce ace = Assert.Single(aces);
            Assert.Equal(@"C:\data\docs", ace.Path);
            Assert.Equal(AppContainerAccess.ReadExecute, ace.Access);
        }

        [Fact]
        public void BuildAceList_ReadWriteRule_GrantsReadWriteExecute()
        {
            IReadOnlyList<AppContainerAce> aces =
                WindowsAppContainerPolicy.BuildAceList(Plan(false, new ResolvedFsRule(@"C:\data\docs", true)));
            AppContainerAce ace = Assert.Single(aces);
            Assert.Equal(AppContainerAccess.ReadWriteExecute, ace.Access);
        }

        [Fact]
        public void BuildAceList_PreservesEveryRuleInOrder()
        {
            // rules[0] is conventionally the app dir (ro) — it must get an ACE too, or
            // the AppContainer can't read its own binary and the process won't start.
            IReadOnlyList<AppContainerAce> aces = WindowsAppContainerPolicy.BuildAceList(Plan(false,
                new ResolvedFsRule(@"C:\apps\demo", false),
                new ResolvedFsRule(@"C:\data\docs", true)));
            Assert.Equal(2, aces.Count);
            Assert.Equal(@"C:\apps\demo", aces[0].Path);
            Assert.Equal(@"C:\data\docs", aces[1].Path);
        }

        [Fact]
        public void BuildAceList_Unrestricted_GrantsNothing()
        {
            // Host opt-out: the backend spawns directly with no container, so there is
            // no SID to grant and the list must be empty.
            SandboxPlan host = new SandboxPlan(unrestricted: true,
                new[] { new ResolvedFsRule(@"C:\apps\demo", false) });
            Assert.Empty(WindowsAppContainerPolicy.BuildAceList(host));
        }

        [Fact]
        public void BuildCapabilitySids_NetworkAllowed_IncludesInternetClient()
        {
            IReadOnlyList<string> caps =
                WindowsAppContainerPolicy.BuildCapabilitySids(Plan(restrictNetwork: false,
                    new ResolvedFsRule(@"C:\apps\demo", false)));
            Assert.Contains(WindowsAppContainerPolicy.InternetClientCapabilitySid, caps);
        }

        [Fact]
        public void BuildCapabilitySids_NetworkRestricted_IsEmpty()
        {
            // Mirror Landlock/Seatbelt: omit the capability SID to deny network.
            IReadOnlyList<string> caps =
                WindowsAppContainerPolicy.BuildCapabilitySids(Plan(restrictNetwork: true,
                    new ResolvedFsRule(@"C:\apps\demo", false)));
            Assert.Empty(caps);
        }
    }
}
