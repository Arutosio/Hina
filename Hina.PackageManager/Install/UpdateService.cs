using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Configuration;
using Hina.Core.Patching;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Hooks;
using Hina.PackageManager.Io;
using Hina.PackageManager.Paths;
using Hina.PackageManager.Platform;
using Hina.PackageManager.Registry;
using Hina.PackageManager.Sandbox;
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

            // [0] The install dir can be gone if the user deleted it by hand. Patching a missing
            // directory would crash mid-operation; fail cleanly and point at the recovery path.
            if (!Directory.Exists(app.InstallPath))
            {
                return new UpdateResult
                {
                    Name = name,
                    FromVersion = app.InstalledVersion,
                    Status = UpdateStatus.Failed,
                    Message = $"Install directory for '{name}' is missing ({app.InstallPath}). Run `hina reinstall {name}` to restore it."
                };
            }

            // [1] Re-fetch descriptor.
            AppDescriptor descriptor;
            try
            {
                descriptor = await _fetcher.FetchAsync(new Uri(app.DescriptorUrl), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // User cancellation must propagate (UpdateAllAsync aborts on it); only real
                // fetch failures (network, HTTP timeout) become a Failed result.
                return new UpdateResult { Name = name, FromVersion = app.InstalledVersion, Status = UpdateStatus.Failed, Message = ex.Message };
            }

            // [2] Validate + signature pinning.
            DescriptorValidator.Validate(descriptor).EnsureValid();

            // The app's primary identity must not drift on update: the registry row and the
            // descriptor cache are both keyed by the installed name, so accepting a renamed
            // descriptor leaves a row "demo" whose cached descriptor claims "demo2". Refuse,
            // like the pinned-key and downgrade gates above/below.
            if (!string.Equals(descriptor.Name, app.Name, StringComparison.Ordinal))
            {
                return new UpdateResult
                {
                    Name = name,
                    FromVersion = app.InstalledVersion,
                    ToVersion = descriptor.Version,
                    Status = UpdateStatus.Failed,
                    Message = $"Descriptor at {app.DescriptorUrl} now declares name '{descriptor.Name}' but '{app.Name}' is installed. " +
                              $"If the publisher renamed the app, install it fresh with `hina install <url>` and then `hina uninstall {app.Name}`."
                };
            }

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
            // [3b] Anti-rollback: refuse a validly-signed older version unless explicitly allowed.
            // Signatures don't expire, so a replayed/served old release would otherwise pass the
            // signature check above and silently downgrade the user to a known-vulnerable build.
            if (!options.AllowDowngrade && !sameVersion)
            {
                if (HinaVersion.TryCompare(descriptor.Version, app.InstalledVersion, out int sign))
                {
                    if (sign < 0)
                    {
                        return new UpdateResult
                        {
                            Name = name,
                            FromVersion = app.InstalledVersion,
                            ToVersion = descriptor.Version,
                            Status = UpdateStatus.Failed,
                            Message = $"Refusing downgrade {app.InstalledVersion} → {descriptor.Version}. Pass --allow-downgrade to override."
                        };
                    }
                }
                else
                {
                    // Unparseable version on either side (e.g. a hand-edited registry): we can't tell
                    // direction, so downgrade protection can't apply. Proceed but surface a warning.
                    _logger.LogWarning(
                        "Cannot compare versions '{From}' and '{To}' for {Name}; downgrade protection skipped.",
                        app.InstalledVersion, descriptor.Version, name);
                }
            }

            // M5: when sameVersion && Force, we fall through. For an unchanged descriptor
            // the diff below is empty, so this is effectively a re-patch + registry refresh.

            // [4] Compute hook/entry diffs (pure; see UpdateDiff).
            UpdateDiff diff = UpdateDiff.Compute(app, descriptor);
            List<HookEvidence> hooksToRemove = diff.HooksToRemove;
            List<HookAction> hooksToAdd = diff.HooksToAdd;
            List<ShellEntryRecord> entriesToRemove = diff.EntriesToRemove;
            List<ShellEntry> entriesToAdd = diff.EntriesToAdd;

            // [5] Snapshot registry so we can restore on failure. Also load the PREVIOUS
            // descriptor from cache (still the pre-update version until step [8] refreshes it)
            // so a rollback can re-create the entries/hooks it removed — the new descriptor no
            // longer lists them, so it can't be the restore source.
            InstalledApp previousSnapshot = CloneInstalledApp(app);
            AppDescriptor? previousDescriptor = TryLoadCachedDescriptor(name, out bool previousDescriptorAvailable);

            // [5a] Sandbox permission diff. A new version that BROADENS the app's access
            // (new paths, host, ro→rw, new capabilities, or dropping the sandbox) must be
            // consented to — refuse BEFORE touching disk so nothing is half-applied.
            //
            // BUG-006 fix (fail-closed): when the previous descriptor cache is absent or
            // unparseable we cannot establish the baseline sandbox scope at all. Without a
            // baseline, SandboxDiff.Compute(null, new) treats the old side as "unsandboxed",
            // so EVERY transition looks like a narrowing and Broadened is never set — even a
            // genuine broadening. This is true regardless of whether the new version enables
            // a sandbox: old could have been a narrow sandbox (e.g. home/ro) while new is a
            // broad one (host/rw + network), and the null baseline would hide it. Likewise a
            // sandboxed→unsandboxed drop would pass silently. Since no baseline means no
            // diff can be trusted, the gate fails closed for any missing/unreadable baseline
            // and requires explicit --accept-new-permissions to proceed.
            if (!previousDescriptorAvailable && !options.AcceptNewPermissions)
            {
                _logger.LogWarning(
                    "Update of {Name}: previous descriptor cache is missing or unreadable, so the " +
                    "sandbox permission baseline cannot be established and a broadening cannot be " +
                    "ruled out. Re-run with `--accept-new-permissions` to proceed.",
                    name);
                return new UpdateResult
                {
                    Name = name,
                    FromVersion = app.InstalledVersion,
                    ToVersion = descriptor.Version,
                    Status = UpdateStatus.Failed,
                    Message = $"Cannot verify sandbox scope for '{name}' {descriptor.Version}: previous descriptor cache is missing or unreadable. " +
                              "Re-run with `--accept-new-permissions` to allow the update."
                };
            }

            SandboxDiff permDiff = SandboxDiff.Compute(previousDescriptor?.Sandbox, descriptor.Sandbox);
            if (permDiff.Broadened && !options.AcceptNewPermissions)
            {
                _logger.LogWarning("Update of {Name} requests BROADER permissions:", name);
                foreach (string a in permDiff.Added) _logger.LogWarning("  + {Added}", a);
                return new UpdateResult
                {
                    Name = name,
                    FromVersion = app.InstalledVersion,
                    ToVersion = descriptor.Version,
                    Status = UpdateStatus.Failed,
                    Message = $"'{name}' {descriptor.Version} requests broader permissions ({string.Join(", ", permDiff.Added)}). " +
                              "Re-run with `--accept-new-permissions` to allow it."
                };
            }
            if (permDiff.Added.Count > 0 || permDiff.Removed.Count > 0)
            {
                _logger.LogInformation("Permission changes for {Name}:", name);
                foreach (string a in permDiff.Added) _logger.LogInformation("  + {Added}", a);
                foreach (string r in permDiff.Removed) _logger.LogInformation("  - {Removed}", r);
            }

            // [6] PatchClient delta — fetch the SAME variant the app was installed as.
            PlatformResolution resolution = PlatformResolver.ForInstalledToken(descriptor, app.Platform);
            if (resolution.NoBuildForPlatform)
            {
                return new UpdateResult
                {
                    Name = name,
                    FromVersion = app.InstalledVersion,
                    ToVersion = descriptor.Version,
                    Status = UpdateStatus.Failed,
                    Message = "The updated descriptor has no build for this platform."
                };
            }
            PatcherConfig patchCfg = options.Network.ToPatchConfig(
                descriptor.BaseUrl, descriptor.Channel, descriptor.PublicKey, backup: true, platform: resolution.ManifestToken);
            IPatchClient patcher = _patchClientFactory(patchCfg);

            try
            {
                PatchResult patchResult = await patcher.PatchAsync(app.InstallPath, ct);
                if (!patchResult.Success)
                {
                    throw new InvalidOperationException("PatchClient.PatchAsync reported failure.");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Ctrl+C mid-patch: still roll back to a consistent state (with a token that
                // can't be cancelled — cleanup must complete), then let cancellation propagate
                // instead of reporting a Failed result.
                _logger.LogInformation("Update of {Name} cancelled during patch; rolling back.", name);
                try { await patcher.RollbackAsync(app.InstallPath, CancellationToken.None); }
                catch (Exception rbEx) { _logger.LogDebug(rbEx, "Patch rollback failed for {Name} (fail-soft).", name); }
                try { await SaveAppRowLockedAsync(locks, store, name, previousSnapshot, CancellationToken.None); }
                catch (Exception saveEx) { _logger.LogDebug(saveEx, "Registry snapshot restore failed for {Name} (fail-soft).", name); }
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Patch failed for {Name}; rolling back.", name);
                try { await patcher.RollbackAsync(app.InstallPath, ct); }
                catch (Exception rbEx) { _logger.LogDebug(rbEx, "Patch rollback failed for {Name} (fail-soft).", name); }
                await SaveAppRowLockedAsync(locks, store, name, previousSnapshot, ct);
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
                Platform = resolution.ManifestToken ?? string.Empty,
                InstalledAt = app.InstalledAt,
                LastUpdatedAt = DateTimeOffset.UtcNow,
                ExecutedHooks = UpdateDiff.SurvivingHooks(app.ExecutedHooks, hooksToRemove),
                ShellEntries = UpdateDiff.SurvivingEntries(app.ShellEntries, entriesToRemove)
            };

            // B1: bracket removals AND additions so a mid-flight failure (e.g. registerAutostart
            // perm denied) doesn't leak half-applied hooks. On failure we undo what we
            // just added in reverse, best-effort re-restore what we just removed, and
            // restore the pre-update registry snapshot so the user sees a clean rollback.
            // Removal steps stay fail-soft for real errors, but user cancellation escapes
            // them so Ctrl+C rolls back instead of committing a half-diffed state.
            List<ShellEntryRecord> addedEntries = new List<ShellEntryRecord>();
            List<HookEvidence> addedHooks = new List<HookEvidence>();

            try
            {
                foreach (HookEvidence ev in hooksToRemove)
                {
                    try { await hooks.UndoAsync(ev, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogDebug(ex, "Undo of removed hook {Action} failed for {Name} (fail-soft).", ev.Action, name); }
                }
                foreach (ShellEntryRecord r in entriesToRemove)
                {
                    try { await _platform.RemoveMenuShortcut(r.Evidence, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogDebug(ex, "Removal of shell entry {Id} failed for {Name} (fail-soft).", r.Id, name); }
                }

                foreach (ShellEntry entry in entriesToAdd)
                {
                    // Route new entries of a sandboxed app through `hina run` (BUG-043): without
                    // this they would launch the binary directly, bypassing the sandbox.
                    string? launchOverride = LaunchRouting.BuildLaunchOverride(descriptor, entry);
                    string evidence = await _platform.CreateMenuShortcut(entry, app.InstallPath, launchOverride, ct);
                    ShellEntryRecord rec = new ShellEntryRecord { Id = entry.Id, Evidence = evidence };
                    addedEntries.Add(rec);
                    updated.ShellEntries.Add(rec);
                }
                foreach (HookAction hook in hooksToAdd)
                {
                    HookEvidence evidence = await hooks.ApplyAsync(hook, app.InstallPath, app.Name, descriptor.Entries, ct);
                    addedHooks.Add(evidence);
                    updated.ExecutedHooks.Add(evidence);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogInformation("Update of {Name} cancelled during hook/entry changes; rolling back.", name);
                await RollbackFailedAddAsync(
                    name, app, hooks, patcher, previousDescriptor,
                    addedHooks, addedEntries, entriesToRemove, hooksToRemove,
                    locks, store, previousSnapshot);
                throw;
            }
            catch (Exception addEx)
            {
                _logger.LogError(addEx, "Hook/entry add failed for {Name}; rolling back update.", name);

                await RollbackFailedAddAsync(
                    name, app, hooks, patcher, previousDescriptor,
                    addedHooks, addedEntries, entriesToRemove, hooksToRemove,
                    locks, store, previousSnapshot);

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
            // This is the commit point and runs on CancellationToken.None: all the work
            // is already done (files, hooks, entries are at v2), so a Ctrl+C in this
            // window must not abort the write — it would leave files at v2 with the
            // registry still claiming v1, and the swallowed cancellation surfaced as a
            // bogus "registry could not be saved: operation was canceled" failure.
            // Bounded: AcquireAsync gives up with a TimeoutException after its max wait.
            try
            {
                using RegistryLock writeLock = await locks.AcquireAsync(CancellationToken.None);
                registry = store.Load();
                registry.Apps[name] = updated;
                await store.SaveAsync(registry, CancellationToken.None);
            }
            catch (Exception saveEx)
            {
                string recoveryPath = _paths.RegistryFile + ".recovery.json";
                try
                {
                    // BUG-014 fix: `registry` may be the stale pre-update snapshot if
                    // AcquireAsync or store.Load() above threw before we could assign
                    // `registry.Apps[name] = updated`. Rebuild the recovery object here
                    // so it always contains the v2 row (`updated`), regardless of which
                    // step inside the try failed. We re-attempt Load() (best-effort) to
                    // also capture sibling-app rows written by concurrent workers; if that
                    // fails too we fall back to the pre-update registry. Either way we
                    // unconditionally inject the v2 row so the snapshot is never stale.
                    Registry.Registry recoveryRegistry;
                    try { recoveryRegistry = store.Load(); }
                    catch { recoveryRegistry = registry; }
                    recoveryRegistry.Apps[name] = updated;

                    await File.WriteAllTextAsync(
                        recoveryPath,
                        System.Text.Json.JsonSerializer.Serialize(
                            recoveryRegistry,
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

            // [8] Refresh descriptor cache (atomic, best-effort).
            RefreshDescriptorCacheBestEffort(name, descriptor);

            return new UpdateResult
            {
                Name = name,
                FromVersion = app.InstalledVersion,
                ToVersion = descriptor.Version,
                Status = UpdateStatus.Updated
            };
        }

        // Rewrite the cached descriptor so run/perms see the new version's scope. Not fatal
        // if it fails — the previous version's cache already exists, so the worst case is
        // run/perms showing stale scope until the next update/reinstall — but log it rather
        // than swallow silently.
        private void RefreshDescriptorCacheBestEffort(string name, AppDescriptor descriptor)
        {
            try
            {
                AtomicFile.WriteAllText(_paths.DescriptorCache(name), DescriptorParser.Serialize(descriptor));
            }
            catch (Exception cacheEx)
            {
                _logger.LogWarning(cacheEx, "Updated {Name} but could not refresh its descriptor cache; run/perms may show stale scope until the next update.", name);
            }
        }

        // Recover from a mid-flight failure while applying the new version's hooks/entries:
        // undo what we just added (reverse order), best-effort re-restore the hooks/entries we
        // removed at step [7] from the PREVIOUS descriptor, roll back the on-disk patch, and
        // restore the pre-update registry snapshot — so the user sees a clean rollback. Every
        // step is fail-soft; recovery must not throw.
        private async Task RollbackFailedAddAsync(
            string name, InstalledApp app, HookExecutor hooks, IPatchClient patcher, AppDescriptor? previousDescriptor,
            List<HookEvidence> addedHooks, List<ShellEntryRecord> addedEntries,
            List<ShellEntryRecord> entriesToRemove, List<HookEvidence> hooksToRemove,
            LockManager locks, RegistryStore store, InstalledApp previousSnapshot)
        {
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
            // Best-effort: re-apply the hooks/entries we removed at step [7] so the app
            // comes back to its pre-update side-effect set. These are sourced from the
            // PREVIOUS descriptor (the new one no longer lists them). May silently fail
            // (e.g. target re-exists, race) — we proceed regardless.
            if (previousDescriptor != null)
            {
                foreach (ShellEntryRecord r in entriesToRemove)
                {
                    foreach (ShellEntry orig in previousDescriptor.Entries)
                    {
                        if (orig.Id != r.Id) continue;
                        // Re-create with the same sandbox routing the previous descriptor had,
                        // so a rollback can't downgrade a re-created entry to unsandboxed (BUG-043).
                        string? rollbackOverride = LaunchRouting.BuildLaunchOverride(previousDescriptor, orig);
                        try { await _platform.CreateMenuShortcut(orig, app.InstallPath, rollbackOverride, CancellationToken.None); }
                        catch (Exception ex) { _logger.LogDebug(ex, "Re-create of shell entry {Id} during rollback failed for {Name} (fail-soft).", r.Id, name); }
                        break;
                    }
                }
                foreach (HookEvidence removed in hooksToRemove)
                {
                    string removedId = UpdateDiff.ResolveIdentity(removed);
                    foreach (HookAction origHook in previousDescriptor.PostInstall)
                    {
                        if (HookIdentity.For(origHook) != removedId) continue;
                        try { await hooks.ApplyAsync(origHook, app.InstallPath, app.Name, previousDescriptor.Entries, CancellationToken.None); }
                        catch (Exception ex) { _logger.LogDebug(ex, "Re-apply of hook {Id} during rollback failed for {Name} (fail-soft).", removedId, name); }
                        break;
                    }
                }
            }
            // Patch on disk also needs to roll back; PatchClient already journaled
            // backups when Backup=true so RollbackAsync restores them.
            try { await patcher.RollbackAsync(app.InstallPath, CancellationToken.None); }
            catch (Exception ex) { _logger.LogDebug(ex, "Patch rollback during add-failure recovery failed for {Name} (fail-soft).", name); }

            try { await SaveAppRowLockedAsync(locks, store, name, previousSnapshot, CancellationToken.None); }
            catch (Exception ex) { _logger.LogWarning(ex, "Restoring pre-update registry snapshot for {Name} failed (fail-soft); run `hina verify`.", name); }
        }

        // Write a single app row under the exclusive lock, re-reading the on-disk registry first so
        // we merge with rows that other concurrent UpdateAsync workers wrote. The rollback paths must
        // use this (not a lock-free SaveAsync of the stale in-memory registry) or a failing update in
        // `update --all` would clobber a sibling app's freshly-written row.
        private static async Task SaveAppRowLockedAsync(
            LockManager locks, RegistryStore store, string name, InstalledApp row, CancellationToken ct)
        {
            using RegistryLock l = await locks.AcquireAsync(ct);
            Registry.Registry reg = await store.LoadAsync(ct);
            reg.Apps[name] = row;
            await store.SaveAsync(reg, ct);
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
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // One app throwing (e.g. its re-fetched descriptor fails validation)
                        // must not fault the whole run and discard every other app's result.
                        results[idx] = new UpdateResult { Name = names[idx], Status = UpdateStatus.Failed, Message = ex.Message };
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

        // Loads the descriptor cached at install/last-update time (the pre-update version).
        // Returns null if absent or unparseable — callers treat that as "no restore source".
        // `available` is set to false when the file is absent or unreadable/unparseable,
        // and to true only when a descriptor was successfully loaded. Callers that need to
        // distinguish "no sandbox declared" from "cache missing" (BUG-006) use this flag.
        private AppDescriptor? TryLoadCachedDescriptor(string name, out bool available)
        {
            try
            {
                string path = _paths.DescriptorCache(name);
                if (!File.Exists(path))
                {
                    available = false;
                    return null;
                }
                AppDescriptor d = DescriptorParser.Parse(File.ReadAllText(path));
                available = true;
                return d;
            }
            catch
            {
                available = false;
                return null;
            }
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
            Platform = src.Platform,
            InstalledAt = src.InstalledAt,
            LastUpdatedAt = src.LastUpdatedAt,
            ExecutedHooks = new List<HookEvidence>(src.ExecutedHooks),
            ShellEntries = new List<ShellEntryRecord>(src.ShellEntries)
        };

        // H2 helper, kept on UpdateService as a stable public entry point for tests (which
        // live in a separate assembly). Delegates to UpdateDiff where the logic now lives.
        public static string ResolveIdentity(HookEvidence ev) => UpdateDiff.ResolveIdentity(ev);
    }
}
