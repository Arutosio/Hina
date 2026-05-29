using System;
using System.Collections.Generic;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Hooks;
using Hina.PackageManager.Registry;

namespace Hina.PackageManager.Install
{
    // The hook/entry delta between an installed app and a freshly fetched descriptor.
    // Pure (no IO), so the diff logic is unit-testable in isolation from UpdateService's
    // patch/rollback orchestration (#5). UpdateService consumes Compute() and the
    // Surviving* helpers; ResolveIdentity is also surfaced through UpdateService for tests.
    internal sealed class UpdateDiff
    {
        public List<HookEvidence> HooksToRemove { get; }
        public List<HookAction> HooksToAdd { get; }
        public List<ShellEntryRecord> EntriesToRemove { get; }
        public List<ShellEntry> EntriesToAdd { get; }

        private UpdateDiff(
            List<HookEvidence> hooksToRemove,
            List<HookAction> hooksToAdd,
            List<ShellEntryRecord> entriesToRemove,
            List<ShellEntry> entriesToAdd)
        {
            HooksToRemove = hooksToRemove;
            HooksToAdd = hooksToAdd;
            EntriesToRemove = entriesToRemove;
            EntriesToAdd = entriesToAdd;
        }

        // Diff hooks by stable identity and entries by id.
        // H2: HookEvidence.Identity was added in Phase 3; registry rows written by an earlier
        // Hina have an empty Identity. ResolveIdentity synthesizes one so the diff stays stable
        // on those legacy rows (addToPath recovers exact identity; other actions get a
        // deterministic synthetic and self-heal on the first update).
        public static UpdateDiff Compute(InstalledApp app, AppDescriptor descriptor)
        {
            HashSet<string> existingHookIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (HookEvidence ev in app.ExecutedHooks) existingHookIds.Add(ResolveIdentity(ev));
            HashSet<string> newHookIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (HookAction hook in descriptor.PostInstall) newHookIds.Add(HookIdentity.For(hook));

            List<HookEvidence> hooksToRemove = new();
            foreach (HookEvidence ev in app.ExecutedHooks)
            {
                if (!newHookIds.Contains(ResolveIdentity(ev))) hooksToRemove.Add(ev);
            }
            List<HookAction> hooksToAdd = new();
            foreach (HookAction hook in descriptor.PostInstall)
            {
                if (!existingHookIds.Contains(HookIdentity.For(hook))) hooksToAdd.Add(hook);
            }

            HashSet<string> existingEntryIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (ShellEntryRecord r in app.ShellEntries) existingEntryIds.Add(r.Id);
            HashSet<string> newEntryIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (ShellEntry e in descriptor.Entries) newEntryIds.Add(e.Id);

            List<ShellEntryRecord> entriesToRemove = new();
            foreach (ShellEntryRecord r in app.ShellEntries)
            {
                if (!newEntryIds.Contains(r.Id)) entriesToRemove.Add(r);
            }
            List<ShellEntry> entriesToAdd = new();
            foreach (ShellEntry e in descriptor.Entries)
            {
                if (!existingEntryIds.Contains(e.Id)) entriesToAdd.Add(e);
            }

            return new UpdateDiff(hooksToRemove, hooksToAdd, entriesToRemove, entriesToAdd);
        }

        // H2 helper. Falls back to a derived identity for legacy registry rows written before
        // HookEvidence.Identity existed (Phase 3).
        public static string ResolveIdentity(HookEvidence ev)
        {
            if (!string.IsNullOrEmpty(ev.Identity)) return ev.Identity;
            switch (ev.Action)
            {
                case "addToPath":
                    // Evidence is "<binDir>/<name>" (Linux/macOS) or "<binDir>\\<name>.cmd"
                    // (Windows). Strip directory + extension manually to recover the original
                    // AddToPathHook.Name regardless of which OS originally wrote the row.
                    string basename = ev.Evidence;
                    int lastSep = basename.LastIndexOfAny(new[] { '/', '\\' });
                    if (lastSep >= 0) basename = basename.Substring(lastSep + 1);
                    int lastDot = basename.LastIndexOf('.');
                    if (lastDot > 0) basename = basename.Substring(0, lastDot);
                    return "addToPath:" + basename;
                default:
                    // Exact reconstruction isn't possible for these actions from evidence
                    // alone. Use a deterministic synthetic so legacy rows remain stable within
                    // this single update; the next update will see proper Identity values
                    // written by the new HookExecutor.
                    return "legacy:" + ev.Action + ":" + Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes(ev.Evidence)))
                        .Substring(0, 8);
            }
        }

        public static List<HookEvidence> SurvivingHooks(List<HookEvidence> existing, List<HookEvidence> removed)
        {
            HashSet<string> removedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (HookEvidence r in removed) removedIds.Add(ResolveIdentity(r));

            List<HookEvidence> kept = new List<HookEvidence>();
            foreach (HookEvidence ev in existing)
            {
                if (!removedIds.Contains(ResolveIdentity(ev))) kept.Add(ev);
            }
            return kept;
        }

        public static List<ShellEntryRecord> SurvivingEntries(List<ShellEntryRecord> existing, List<ShellEntryRecord> removed)
        {
            HashSet<string> removedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (ShellEntryRecord r in removed) removedIds.Add(r.Id);

            List<ShellEntryRecord> kept = new List<ShellEntryRecord>();
            foreach (ShellEntryRecord r in existing)
            {
                if (!removedIds.Contains(r.Id)) kept.Add(r);
            }
            return kept;
        }
    }
}
