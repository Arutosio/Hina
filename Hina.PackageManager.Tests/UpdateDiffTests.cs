using System.Collections.Generic;
using System.Linq;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Hooks;
using Hina.PackageManager.Install;
using Hina.PackageManager.Registry;

namespace Hina.PackageManager.Tests
{
    // Unit tests for the diff logic extracted from UpdateService (#5). Pure computation —
    // no IO — so these run with no filesystem/network.
    public class UpdateDiffTests
    {
        private static InstalledApp App(IEnumerable<HookEvidence>? hooks = null, IEnumerable<ShellEntryRecord>? entries = null)
            => new InstalledApp
            {
                Name = "demo",
                InstalledVersion = "1.0.0",
                ExecutedHooks = (hooks ?? Enumerable.Empty<HookEvidence>()).ToList(),
                ShellEntries = (entries ?? Enumerable.Empty<ShellEntryRecord>()).ToList()
            };

        private static AppDescriptor Desc(IEnumerable<HookAction>? hooks = null, IEnumerable<ShellEntry>? entries = null)
            => new AppDescriptor
            {
                Name = "demo",
                Version = "2.0.0",
                PostInstall = (hooks ?? Enumerable.Empty<HookAction>()).ToList(),
                Entries = (entries ?? Enumerable.Empty<ShellEntry>()).ToList()
            };

        [Fact]
        public void NewHookInDescriptor_IsAdded()
        {
            UpdateDiff diff = UpdateDiff.Compute(
                App(),
                Desc(hooks: new[] { new AddToPathHook { Name = "mycli", Target = "bin/mycli" } }));

            Assert.Single(diff.HooksToAdd);
            Assert.Empty(diff.HooksToRemove);
            Assert.Equal("addToPath:mycli", HookIdentity.For(diff.HooksToAdd[0]));
        }

        [Fact]
        public void ExistingHookMissingFromDescriptor_IsRemoved()
        {
            HookEvidence existing = new HookEvidence { Action = "addToPath", Identity = "addToPath:old", Evidence = "/bin/old" };

            UpdateDiff diff = UpdateDiff.Compute(App(hooks: new[] { existing }), Desc());

            Assert.Single(diff.HooksToRemove);
            Assert.Empty(diff.HooksToAdd);
            Assert.Equal("addToPath:old", diff.HooksToRemove[0].Identity);
        }

        [Fact]
        public void UnchangedHook_IsNeitherAddedNorRemoved()
        {
            HookEvidence existing = new HookEvidence { Action = "addToPath", Identity = "addToPath:keep", Evidence = "/bin/keep" };

            UpdateDiff diff = UpdateDiff.Compute(
                App(hooks: new[] { existing }),
                Desc(hooks: new[] { new AddToPathHook { Name = "keep", Target = "bin/keep" } }));

            Assert.Empty(diff.HooksToAdd);
            Assert.Empty(diff.HooksToRemove);
        }

        [Fact]
        public void LegacyHookWithoutIdentity_MatchesByDerivedIdentity()
        {
            // No Identity (pre-Phase-3 row). ResolveIdentity must recover "addToPath:keep"
            // from the evidence path so the unchanged hook isn't churned.
            HookEvidence legacy = new HookEvidence { Action = "addToPath", Identity = "", Evidence = "/home/u/.local/bin/keep" };

            UpdateDiff diff = UpdateDiff.Compute(
                App(hooks: new[] { legacy }),
                Desc(hooks: new[] { new AddToPathHook { Name = "keep", Target = "bin/keep" } }));

            Assert.Empty(diff.HooksToAdd);
            Assert.Empty(diff.HooksToRemove);
        }

        [Fact]
        public void Entries_DiffedById()
        {
            ShellEntryRecord existing = new ShellEntryRecord { Id = "main", Evidence = "/apps/main.desktop" };

            UpdateDiff diff = UpdateDiff.Compute(
                App(entries: new[] { existing }),
                Desc(entries: new[] { new ShellEntry { Id = "secondary" } }));

            Assert.Single(diff.EntriesToAdd);
            Assert.Equal("secondary", diff.EntriesToAdd[0].Id);
            Assert.Single(diff.EntriesToRemove);
            Assert.Equal("main", diff.EntriesToRemove[0].Id);
        }

        [Fact]
        public void SurvivingHooks_ExcludesRemoved()
        {
            HookEvidence keep = new HookEvidence { Action = "addToPath", Identity = "addToPath:keep", Evidence = "/bin/keep" };
            HookEvidence drop = new HookEvidence { Action = "addToPath", Identity = "addToPath:drop", Evidence = "/bin/drop" };

            List<HookEvidence> kept = UpdateDiff.SurvivingHooks(
                new List<HookEvidence> { keep, drop },
                new List<HookEvidence> { drop });

            Assert.Single(kept);
            Assert.Equal("addToPath:keep", kept[0].Identity);
        }
    }
}
