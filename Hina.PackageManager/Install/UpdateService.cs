using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Configuration;
using Hina.Core.Patching;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Hooks;
using Hina.PackageManager.Paths;
using Hina.PackageManager.Platform;
using Hina.PackageManager.Registry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hina.PackageManager.Install
{
    // `hina update [name]`. Re-fetches the publisher's descriptor, verifies it against the
    // key pinned at install time, diffs hooks/entries, runs the delta patcher, and applies
    // the diff. On post-patch failure runs PatchClient.RollbackAsync and restores the
    // previous registry snapshot.
    public sealed class UpdateService
    {
        private readonly InstallPaths _paths;
        private readonly IPlatformIntegration _platform;
        private readonly DescriptorFetcher _fetcher;
        private readonly Func<PatcherConfig, IPatchClient> _patchClientFactory;
        private readonly ILogger _logger;

        public UpdateService(
            InstallPaths paths,
            IPlatformIntegration platform,
            DescriptorFetcher? fetcher = null,
            Func<PatcherConfig, IPatchClient>? patchClientFactory = null,
            ILogger? logger = null)
        {
            _paths = paths;
            _platform = platform;
            _fetcher = fetcher ?? new DescriptorFetcher();
            _patchClientFactory = patchClientFactory ?? (cfg => new PatchClient(cfg));
            _logger = logger ?? NullLogger.Instance;
        }

        public async Task<UpdateResult> UpdateAsync(string name, UpdateOptions? options, CancellationToken ct)
        {
            options ??= new UpdateOptions();

            // O2: hold the registry lock only for the brief read/write windows so
            // parallel UpdateAllAsync isn't serialized by lock contention. The slow
            // work (descriptor fetch, PatchClient, hook application) runs lock-free.
            LockManager locks = new LockManager(_paths.LockFile);
            RegistryStore store = new RegistryStore(_paths.RegistryFile);
            Registry.Registry registry;
            InstalledApp app;
            {
                using RegistryLock l = await locks.AcquireAsync(ct);
                registry = store.Load();
                if (!registry.Apps.TryGetValue(name, out InstalledApp? found))
                {
                    return new UpdateResult { Name = name, Status = UpdateStatus.Failed, Message = $"'{name}' is not installed." };
                }
                app = found;
            }

            // [1] Re-fetch descriptor.
            AppDescriptor descriptor;
            try
            {
                descriptor = await _fetcher.FetchAsync(new Uri(app.DescriptorUrl), ct);
            }
            catch (Exception ex)
            {
                return new UpdateResult { Name = name, FromVersion = app.InstalledVersion, Status = UpdateStatus.Failed, Message = ex.Message };
            }

            // [2] Validate + signature pinning.
            DescriptorValidator.Validate(descriptor).EnsureValid();

            // [2a] Same minHinaVersion gate as InstallService — block an update that would
            //      drag the install into a state this Hina can't drive.
            if (!string.IsNullOrWhiteSpace(descriptor.MinHinaVersion) && !HinaVersion.IsSatisfiedBy(descriptor.MinHinaVersion))
            {
                return new UpdateResult
                {
                    Name = name,
                    FromVersion = app.InstalledVersion,
                    ToVersion = descriptor.Version,
                    Status = UpdateStatus.Failed,
                    Message = $"App requires Hina {descriptor.MinHinaVersion} or newer; running {HinaVersion.Current}. Upgrade Hina first."
                };
            }

            string verifyingKey = options.AllowRotateKey ? descriptor.PublicKey : app.PublicKey;
            if (!DescriptorSigner.Verify(descriptor, verifyingKey))
            {
                return new UpdateResult
                {
                    Name = name,
                    FromVersion = app.InstalledVersion,
                    Status = UpdateStatus.Failed,
                    Message = options.AllowRotateKey
                        ? "Descriptor signature does not match its own declared publicKey."
                        : "Descriptor signature does not match the pinned publisher key. Use `hina reinstall --rotate-key` to accept a new key."
                };
            }

            // [3] Skip if already up to date and not forced.
            bool sameVersion = string.Equals(descriptor.Version, app.InstalledVersion, StringComparison.Ordinal);
            if (sameVersion && !options.Force)
            {
                return new UpdateResult
                {
                    Name = name,
                    FromVersion = app.InstalledVersion,
                    ToVersion = descriptor.Version,
                    Status = UpdateStatus.AlreadyUpToDate
                };
            }
            // M5: when sameVersion && Force, we fall through. For an unchanged descriptor
            // the diff below is empty, so this is effectively a re-patch + registry refresh.

            // [4] Compute diffs (by hook identity and entry id).
            // H2: HookEvidence.Identity was added in Phase 3; registry rows written by
            // an earlier Hina have an empty Identity. ResolveIdentity synthesizes one
            // so the diff stays stable on those legacy rows (addToPath recovers exact
            // identity; other actions get a deterministic synthetic and self-heal on
            // the first update).
            HashSet<string> existingHookIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (HookEvidence ev in app.ExecutedHooks)
            {
                existingHookIds.Add(ResolveIdentity(ev));
            }
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

            // [5] Snapshot registry so we can restore on failure.
            InstalledApp previousSnapshot = CloneInstalledApp(app);

            // [6] PatchClient delta.
            PatcherConfig patchCfg = new PatcherConfig
            {
                BaseUrl = new Uri(descriptor.BaseUrl),
                Channel = descriptor.Channel,
                TrustedPublicKey = descriptor.PublicKey,
                Verify = true,
                Backup = true,
                MaxRetries = options.Network.MaxRetries,
                RetryBaseDelayMs = options.Network.RetryBaseDelayMs,
                MaxRetryDelayMs = options.Network.MaxRetryDelayMs,
                ConnectTimeoutMs = options.Network.ConnectTimeoutMs,
                RequestTimeoutMs = options.Network.RequestTimeoutMs
            };
            IPatchClient patcher = _patchClientFactory(patchCfg);

            try
            {
                PatchResult patchResult = await patcher.PatchAsync(app.InstallPath, ct);
                if (!patchResult.Success)
                {
                    throw new InvalidOperationException("PatchClient.PatchAsync reported failure.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Patch failed for {Name}; rolling back.", name);
                try { await patcher.RollbackAsync(app.InstallPath, ct); }
                catch (Exception rbEx) { _logger.LogDebug(rbEx, "Patch rollback failed for {Name} (fail-soft).", name); }
                registry.Apps[name] = previousSnapshot;
                await store.SaveAsync(registry, ct);
                return new UpdateResult
                {
                    Name = name,
                    FromVersion = app.InstalledVersion,
                    ToVersion = descriptor.Version,
                    Status = UpdateStatus.Failed,
                    Message = ex.Message
                };
            }

            // [7] Apply diffs. Remove first (so an addToPath with the same target name doesn't collide).
            HookExecutor hooks = new HookExecutor(_platform, _logger);

            foreach (HookEvidence ev in hooksToRemove)
            {
                try { await hooks.UndoAsync(ev, ct); }
                catch (Exception ex) { _logger.LogDebug(ex, "Undo of removed hook {Action} failed for {Name} (fail-soft).", ev.Action, name); }
            }
            foreach (ShellEntryRecord r in entriesToRemove)
            {
                try { await _platform.RemoveMenuShortcut(r.Evidence, ct); }
                catch (Exception ex) { _logger.LogDebug(ex, "Removal of shell entry {Id} failed for {Name} (fail-soft).", r.Id, name); }
            }

            // Build updated registry entry.
            InstalledApp updated = new InstalledApp
            {
                Name = app.Name,
                InstalledVersion = descriptor.Version,
                InstallPath = app.InstallPath,
                DescriptorUrl = app.DescriptorUrl,
                BaseUrl = descriptor.BaseUrl,
                Channel = descriptor.Channel,
                PublicKey = descriptor.PublicKey,
                InstalledAt = app.InstalledAt,
                LastUpdatedAt = DateTimeOffset.UtcNow,
                ExecutedHooks = SurvivingHooks(app.ExecutedHooks, hooksToRemove),
                ShellEntries = SurvivingEntries(app.ShellEntries, entriesToRemove)
            };

            // B1: bracket the additions so a mid-flight failure (e.g. registerAutostart
            // perm denied) doesn't leak half-applied hooks. On failure we undo what we
            // just added in reverse, best-effort re-restore what we just removed, and
            // restore the pre-update registry snapshot so the user sees a clean rollback.
            List<ShellEntryRecord> addedEntries = new List<ShellEntryRecord>();
            List<HookEvidence> addedHooks = new List<HookEvidence>();

            try
            {
                foreach (ShellEntry entry in entriesToAdd)
                {
                    string evidence = await _platform.CreateMenuShortcut(entry, app.InstallPath, ct);
                    ShellEntryRecord rec = new ShellEntryRecord { Id = entry.Id, Evidence = evidence };
                    addedEntries.Add(rec);
                    updated.ShellEntries.Add(rec);
                }
                foreach (HookAction hook in hooksToAdd)
                {
                    HookEvidence evidence = await hooks.ApplyAsync(hook, app.InstallPath, app.Name, ct);
                    addedHooks.Add(evidence);
                    updated.ExecutedHooks.Add(evidence);
                }
            }
            catch (Exception addEx)
            {
                _logger.LogError(addEx, "Hook/entry add failed for {Name}; rolling back update.", name);

                for (int i = addedHooks.Count - 1; i >= 0; i--)
                {
                    try { await hooks.UndoAsync(addedHooks[i], CancellationToken.None); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Rollback-undo of added hook {Action} failed for {Name} (fail-soft).", addedHooks[i].Action, name); }
                }
                for (int i = addedEntries.Count - 1; i >= 0; i--)
                {
                    try { await _platform.RemoveMenuShortcut(addedEntries[i].Evidence, CancellationToken.None); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Rollback-removal of added shell entry {Id} failed for {Name} (fail-soft).", addedEntries[i].Id, name); }
                }
                // Best-effort: re-apply the hooks/entries we removed at step [7] so the
                // app comes back to its pre-update side-effect set. May silently fail
                // (e.g. target re-exists, race) — we proceed regardless.
                foreach (ShellEntryRecord r in entriesToRemove)
                {
                    foreach (ShellEntry orig in descriptor.Entries)
                    {
                        if (orig.Id != r.Id) continue;
                        try { await _platform.CreateMenuShortcut(orig, app.InstallPath, CancellationToken.None); }
                        catch (Exception ex) { _logger.LogDebug(ex, "Re-create of shell entry {Id} during rollback failed for {Name} (fail-soft).", r.Id, name); }
                        break;
                    }
                }
                // Patch on disk also needs to roll back; PatchClient already journaled
                // backups when Backup=true so RollbackAsync restores them.
                try { await patcher.RollbackAsync(app.InstallPath, CancellationToken.None); }
                catch (Exception ex) { _logger.LogDebug(ex, "Patch rollback during add-failure recovery failed for {Name} (fail-soft).", name); }

                registry.Apps[name] = previousSnapshot;
                try { await store.SaveAsync(registry, CancellationToken.None); }
                catch (Exception ex) { _logger.LogWarning(ex, "Restoring pre-update registry snapshot for {Name} failed (fail-soft); run `hina verify`.", name); }

                return new UpdateResult
                {
                    Name = name,
                    FromVersion = app.InstalledVersion,
                    ToVersion = descriptor.Version,
                    Status = UpdateStatus.Failed,
                    Message = $"Update rolled back: {addEx.Message}"
                };
            }

            // O2: re-acquire the lock briefly to write. Re-read the registry first
            // so we merge with any changes other concurrent operations made to OTHER
            // apps; we then overwrite our own app's row with the updated entry.
            // H1: if SaveAsync still fails, dump a recovery snapshot.
            try
            {
                using RegistryLock writeLock = await locks.AcquireAsync(ct);
                registry = store.Load();
                registry.Apps[name] = updated;
                await store.SaveAsync(registry, ct);
            }
            catch (Exception saveEx)
            {
                string recoveryPath = _paths.RegistryFile + ".recovery.json";
                try
                {
                    await File.WriteAllTextAsync(
                        recoveryPath,
                        System.Text.Json.JsonSerializer.Serialize(
                            registry,
                            Hina.PackageManager.Json.PackageManagerIndentedJsonContext.Default.Registry),
                        CancellationToken.None);
                }
                catch (Exception dumpEx) { _logger.LogDebug(dumpEx, "Writing recovery snapshot for {Name} failed (fail-soft); original save failure is what matters.", name); }

                _logger.LogError(saveEx,
                    "Update of {Name} patched files on disk but the registry write failed. " +
                    "A recovery snapshot was written to {RecoveryPath}.", name, recoveryPath);

                return new UpdateResult
                {
                    Name = name,
                    FromVersion = app.InstalledVersion,
                    ToVersion = descriptor.Version,
                    Status = UpdateStatus.Failed,
                    Message = $"Files updated but registry could not be saved: {saveEx.Message}. " +
                              $"Recovery snapshot at {recoveryPath}; rename it over registry.json after fixing the underlying issue."
                };
            }

            // [8] Refresh descriptor cache.
            try
            {
                Directory.CreateDirectory(_paths.DescriptorCacheRoot);
                await File.WriteAllTextAsync(_paths.DescriptorCache(name), DescriptorParser.Serialize(descriptor), ct);
            }
            catch { /* non-critical */ }

            return new UpdateResult
            {
                Name = name,
                FromVersion = app.InstalledVersion,
                ToVersion = descriptor.Version,
                Status = UpdateStatus.Updated
            };
        }

        public async Task<List<UpdateResult>> UpdateAllAsync(UpdateOptions? options, CancellationToken ct)
        {
            options ??= new UpdateOptions();
            int parallelism = Math.Clamp(options.MaxParallelism, 1, 16);

            RegistryStore store = new RegistryStore(_paths.RegistryFile);
            List<string> names;
            {
                // Snapshot the name list under a quick lock; per-app updates each take their own lock.
                LockManager locks = new LockManager(_paths.LockFile);
                using RegistryLock l = await locks.AcquireAsync(ct);
                Registry.Registry registry = store.Load();
                names = new List<string>(registry.Apps.Keys);
            }

            // O2: run N updates concurrently. Each per-app UpdateAsync still serializes
            // its own registry-lock window so a parallel update of B doesn't see a
            // half-committed A. The semaphore bounds concurrent network + disk pressure.
            UpdateResult[] results = new UpdateResult[names.Count];
            using SemaphoreSlim gate = new SemaphoreSlim(parallelism);
            Task[] tasks = new Task[names.Count];
            for (int i = 0; i < names.Count; i++)
            {
                int idx = i;
                tasks[i] = Task.Run(async () =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        results[idx] = await UpdateAsync(names[idx], options, ct);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, ct);
            }
            await Task.WhenAll(tasks);
            return new List<UpdateResult>(results);
        }

        private static InstalledApp CloneInstalledApp(InstalledApp src) => new InstalledApp
        {
            Name = src.Name,
            InstalledVersion = src.InstalledVersion,
            InstallPath = src.InstallPath,
            DescriptorUrl = src.DescriptorUrl,
            BaseUrl = src.BaseUrl,
            Channel = src.Channel,
            PublicKey = src.PublicKey,
            InstalledAt = src.InstalledAt,
            LastUpdatedAt = src.LastUpdatedAt,
            ExecutedHooks = new List<HookEvidence>(src.ExecutedHooks),
            ShellEntries = new List<ShellEntryRecord>(src.ShellEntries)
        };

        private static List<HookEvidence> SurvivingHooks(List<HookEvidence> existing, List<HookEvidence> removed)
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

        // H2 helper. Falls back to a derived identity for legacy registry rows that
        // were written before HookEvidence.Identity existed (Phase 3). Public so tests
        // (which live in a separate assembly) can exercise the legacy-mapping rules.
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
                    // alone. Use a deterministic synthetic so legacy rows remain stable
                    // within this single update; the next update will see proper Identity
                    // values written by the new HookExecutor.
                    return "legacy:" + ev.Action + ":" + Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes(ev.Evidence)))
                        .Substring(0, 8);
            }
        }

        private static List<ShellEntryRecord> SurvivingEntries(List<ShellEntryRecord> existing, List<ShellEntryRecord> removed)
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
