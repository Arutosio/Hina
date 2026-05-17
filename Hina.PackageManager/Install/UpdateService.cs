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

            LockManager locks = new LockManager(_paths.LockFile);
            using RegistryLock l = await locks.AcquireAsync(ct);

            RegistryStore store = new RegistryStore(_paths.RegistryFile);
            Registry.Registry registry = store.Load();

            if (!registry.Apps.TryGetValue(name, out InstalledApp? app))
            {
                return new UpdateResult { Name = name, Status = UpdateStatus.Failed, Message = $"'{name}' is not installed." };
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

            // [4] Compute diffs (by hook identity and entry id).
            HashSet<string> existingHookIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (HookEvidence ev in app.ExecutedHooks)
            {
                if (!string.IsNullOrEmpty(ev.Identity)) existingHookIds.Add(ev.Identity);
            }
            HashSet<string> newHookIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (HookAction hook in descriptor.PostInstall) newHookIds.Add(HookIdentity.For(hook));

            List<HookEvidence> hooksToRemove = new();
            foreach (HookEvidence ev in app.ExecutedHooks)
            {
                if (!newHookIds.Contains(ev.Identity)) hooksToRemove.Add(ev);
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
                Backup = true
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
                try { await patcher.RollbackAsync(app.InstallPath, ct); } catch { /* fail-soft */ }
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
            HookExecutor hooks = new HookExecutor(_platform);

            foreach (HookEvidence ev in hooksToRemove)
            {
                try { await hooks.UndoAsync(ev, ct); } catch { /* fail-soft */ }
            }
            foreach (ShellEntryRecord r in entriesToRemove)
            {
                try { await _platform.RemoveMenuShortcut(r.Evidence, ct); } catch { /* fail-soft */ }
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

            // Apply additions, recording fresh evidence.
            foreach (ShellEntry entry in entriesToAdd)
            {
                string evidence = await _platform.CreateMenuShortcut(entry, app.InstallPath, ct);
                updated.ShellEntries.Add(new ShellEntryRecord { Id = entry.Id, Evidence = evidence });
            }
            foreach (HookAction hook in hooksToAdd)
            {
                HookEvidence evidence = await hooks.ApplyAsync(hook, app.InstallPath, ct);
                updated.ExecutedHooks.Add(evidence);
            }

            registry.Apps[name] = updated;
            await store.SaveAsync(registry, ct);

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
            RegistryStore store = new RegistryStore(_paths.RegistryFile);
            List<string> names;
            {
                // Snapshot the name list under a quick lock; per-app updates each take their own lock.
                LockManager locks = new LockManager(_paths.LockFile);
                using RegistryLock l = await locks.AcquireAsync(ct);
                Registry.Registry registry = store.Load();
                names = new List<string>(registry.Apps.Keys);
            }

            List<UpdateResult> results = new List<UpdateResult>(names.Count);
            foreach (string name in names)
            {
                results.Add(await UpdateAsync(name, options, ct));
            }
            return results;
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
            foreach (HookEvidence r in removed) removedIds.Add(r.Identity);

            List<HookEvidence> kept = new List<HookEvidence>();
            foreach (HookEvidence ev in existing)
            {
                if (!removedIds.Contains(ev.Identity)) kept.Add(ev);
            }
            return kept;
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
