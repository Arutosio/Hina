using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Hooks;
using Hina.PackageManager.Install;
using Hina.PackageManager.Paths;
using Hina.PackageManager.Platform;
using Hina.PackageManager.Registry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hina.PackageManager.Diagnostics
{
    // Reconciles the local registry against on-disk state.
    //
    // `Inspect` is read-only: walks every installed app, reports whether its
    // install directory exists and whether each recorded side-effect (shell
    // entry, hook evidence) is still live according to the active
    // IPlatformIntegration.
    //
    // `Repair` mutates: for apps whose install directory is gone, defers to
    // UninstallService (idempotent, removes orphaned entry + side-effects).
    // For dangling individual hooks / shortcuts on apps that still exist,
    // calls the platform's Remove/Unregister + rewrites the registry entry.
    public sealed class RegistryVerifier
    {
        private readonly InstallPaths _paths;
        private readonly IPlatformIntegration _platform;
        private readonly ILogger _logger;

        public RegistryVerifier(InstallPaths paths, IPlatformIntegration platform, ILogger? logger = null)
        {
            _paths = paths;
            _platform = platform;
            _logger = logger ?? NullLogger.Instance;
        }

        // Pure read. No locks taken; the report is a point-in-time snapshot.
        public List<AppDiagnostic> Inspect(string? appName = null)
        {
            Registry.Registry registry = new RegistryStore(_paths.RegistryFile, _logger).Load();

            List<AppDiagnostic> results = new List<AppDiagnostic>();
            foreach (KeyValuePair<string, InstalledApp> kv in registry.Apps)
            {
                if (appName != null && !string.Equals(kv.Key, appName, StringComparison.Ordinal)) continue;

                InstalledApp app = kv.Value;
                AppDiagnostic d = new AppDiagnostic
                {
                    Name = app.Name,
                    Version = app.InstalledVersion,
                    InstallPath = app.InstallPath,
                    AppDirMissing = !Directory.Exists(app.InstallPath)
                };

                foreach (ShellEntryRecord entry in app.ShellEntries)
                {
                    if (_platform.IsEvidenceDangling("shellEntry", entry.Evidence))
                    {
                        d.DanglingShellEntries.Add(entry);
                    }
                }
                foreach (HookEvidence ev in app.ExecutedHooks)
                {
                    if (_platform.IsEvidenceDangling(ev.Action, ev.Evidence))
                    {
                        d.DanglingHooks.Add(ev);
                    }
                }

                // Local integrity: the cached descriptor tells us which files the app must
                // have. Only meaningful when the dir itself is present.
                if (!d.AppDirMissing)
                {
                    CheckFiles(app, d);
                }
                results.Add(d);
            }
            return results;
        }

        // Confirms the files the cached descriptor says the app must have actually exist.
        // A missing executable / entry file means the install is incomplete (deleted by hand
        // or a botched patch) — reinstall, not repair, is the fix.
        private void CheckFiles(InstalledApp app, AppDiagnostic d)
        {
            string descPath = _paths.DescriptorCache(app.Name);
            if (!File.Exists(descPath))
            {
                d.DescriptorCacheMissing = true;
                return;
            }

            AppDescriptor descriptor;
            try { descriptor = DescriptorParser.Parse(File.ReadAllText(descPath)); }
            catch { d.DescriptorCacheMissing = true; return; }

            void Require(string? rel)
            {
                if (string.IsNullOrEmpty(rel)) return;
                if (!File.Exists(Path.Combine(app.InstallPath, rel)) && !d.MissingFiles.Contains(rel))
                {
                    d.MissingFiles.Add(rel);
                }
            }

            Require(PlatformResolver.ForInstalledToken(descriptor, app.Platform).ExecRelative);
            foreach (ShellEntry e in descriptor.Entries) Require(e.Exec);
        }

        // Read-only. Hina-managed artifacts on disk that NO registry row references —
        // the leftovers after a manual `registry.json` (or whole-row) deletion that
        // per-app Inspect/Repair can't see. Empty on platforms without an artifact scanner.
        public List<string> FindOrphanArtifacts()
        {
            Registry.Registry registry = new RegistryStore(_paths.RegistryFile, _logger).Load();
            HashSet<string> referenced = ReferencedArtifactPaths(registry);

            List<string> orphans = new List<string>();
            foreach (string path in _platform.EnumerateManagedArtifacts())
            {
                if (!referenced.Contains(path)) orphans.Add(path);
            }
            return orphans;
        }

        // Mutating. Removes the orphan artifacts found above (fail-soft). Takes the lock
        // so the registry read used to compute "referenced" is consistent with writers.
        public async Task<List<string>> RepairOrphanArtifactsAsync(CancellationToken ct)
        {
            LockManager locks = new LockManager(_paths.LockFile);
            using RegistryLock l = await locks.AcquireAsync(ct);

            Registry.Registry registry = new RegistryStore(_paths.RegistryFile, _logger).Load();
            HashSet<string> referenced = ReferencedArtifactPaths(registry);

            List<string> removed = new List<string>();
            foreach (string path in _platform.EnumerateManagedArtifacts())
            {
                if (referenced.Contains(path)) continue;
                // RemoveMenuShortcut is a plain fail-soft file delete; reused for any artifact path.
                try { await _platform.RemoveMenuShortcut(path, ct); removed.Add(path); }
                catch { /* fail-soft */ }
            }
            return removed;
        }

        // Every artifact path the registry still points at. Font hook evidence packs
        // multiple paths joined with '|' (see HookExecutor), so split on it.
        private static HashSet<string> ReferencedArtifactPaths(Registry.Registry registry)
        {
            HashSet<string> referenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (InstalledApp app in registry.Apps.Values)
            {
                foreach (ShellEntryRecord e in app.ShellEntries) referenced.Add(e.Evidence);
                foreach (HookEvidence ev in app.ExecutedHooks)
                {
                    foreach (string p in ev.Evidence.Split('|', StringSplitOptions.RemoveEmptyEntries))
                    {
                        referenced.Add(p);
                    }
                }
            }
            return referenced;
        }

        // Mutating. Takes the registry lock for the duration of repairs.
        public async Task<List<AppRepairResult>> RepairAsync(string? appName, CancellationToken ct)
        {
            LockManager locks = new LockManager(_paths.LockFile);
            using RegistryLock l = await locks.AcquireAsync(ct);

            RegistryStore store = new RegistryStore(_paths.RegistryFile, _logger);
            Registry.Registry registry = store.Load();

            List<AppRepairResult> results = new List<AppRepairResult>();
            HookExecutor hooks = new HookExecutor(_platform, _logger);

            // Snapshot the keys so we can mutate registry.Apps inside the loop.
            List<string> names = new List<string>(registry.Apps.Keys);

            foreach (string n in names)
            {
                if (appName != null && !string.Equals(n, appName, StringComparison.Ordinal)) continue;
                if (!registry.Apps.TryGetValue(n, out InstalledApp? app)) continue;

                AppRepairResult res = new AppRepairResult { Name = n };

                if (!Directory.Exists(app.InstallPath))
                {
                    // App dir gone — collect it now and remove its registry row + residual
                    // side-effects in the post-loop block below (still under this lock), so we
                    // don't mutate registry.Apps while enumerating it.
                    res.RemovedOrphanEntry = true;
                    res.RemovedHooks.AddRange(app.ExecutedHooks);
                    res.RemovedShellEntries.AddRange(app.ShellEntries);
                    results.Add(res);
                    continue;
                }

                // App dir present — selectively clean dangling shortcuts + hooks
                // and rewrite this app's registry row.
                List<ShellEntryRecord> survivingEntries = new List<ShellEntryRecord>();
                foreach (ShellEntryRecord entry in app.ShellEntries)
                {
                    if (_platform.IsEvidenceDangling("shellEntry", entry.Evidence))
                    {
                        try { await _platform.RemoveMenuShortcut(entry.Evidence, ct); } catch { /* fail-soft */ }
                        res.RemovedShellEntries.Add(entry);
                    }
                    else
                    {
                        survivingEntries.Add(entry);
                    }
                }

                List<HookEvidence> survivingHooks = new List<HookEvidence>();
                foreach (HookEvidence ev in app.ExecutedHooks)
                {
                    if (_platform.IsEvidenceDangling(ev.Action, ev.Evidence))
                    {
                        try { await hooks.UndoAsync(ev, ct); } catch { /* fail-soft */ }
                        res.RemovedHooks.Add(ev);
                    }
                    else
                    {
                        survivingHooks.Add(ev);
                    }
                }

                if (res.RemovedShellEntries.Count > 0 || res.RemovedHooks.Count > 0)
                {
                    app.ShellEntries = survivingEntries;
                    app.ExecutedHooks = survivingHooks;
                    registry.Apps[n] = app;
                }

                results.Add(res);
            }

            // Apply the orphan-entry removals (we deferred them so we could do them
            // outside the per-app loop after the snapshot).
            List<string> orphansToRemove = new List<string>();
            foreach (AppRepairResult r in results)
            {
                if (r.RemovedOrphanEntry) orphansToRemove.Add(r.Name);
            }
            foreach (string orphan in orphansToRemove)
            {
                registry.Apps.Remove(orphan);
                // Best-effort cleanup of any still-extant side-effects (no app dir,
                // but symlinks/shortcuts may still exist with dead targets).
                AppRepairResult r = results.Find(x => x.Name == orphan)!;
                foreach (HookEvidence ev in r.RemovedHooks)
                {
                    try { await hooks.UndoAsync(ev, ct); } catch { /* fail-soft */ }
                }
                foreach (ShellEntryRecord entry in r.RemovedShellEntries)
                {
                    try { await _platform.RemoveMenuShortcut(entry.Evidence, ct); } catch { /* fail-soft */ }
                }
                // Also clear descriptor cache for the orphan.
                try
                {
                    string descCache = _paths.DescriptorCache(orphan);
                    if (File.Exists(descCache)) File.Delete(descCache);
                }
                catch { /* fail-soft */ }
            }

            await store.SaveAsync(registry, ct);
            return results;
        }
    }

    public sealed class AppDiagnostic
    {
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string InstallPath { get; set; } = string.Empty;
        public bool AppDirMissing { get; set; }
        public bool DescriptorCacheMissing { get; set; }
        public List<string> MissingFiles { get; } = new List<string>();
        public List<ShellEntryRecord> DanglingShellEntries { get; } = new List<ShellEntryRecord>();
        public List<HookEvidence> DanglingHooks { get; } = new List<HookEvidence>();

        public bool IsHealthy => !AppDirMissing && !DescriptorCacheMissing && MissingFiles.Count == 0
            && DanglingShellEntries.Count == 0 && DanglingHooks.Count == 0;
    }

    public sealed class AppRepairResult
    {
        public string Name { get; set; } = string.Empty;
        public bool RemovedOrphanEntry { get; set; }
        public List<ShellEntryRecord> RemovedShellEntries { get; } = new List<ShellEntryRecord>();
        public List<HookEvidence> RemovedHooks { get; } = new List<HookEvidence>();
    }
}
