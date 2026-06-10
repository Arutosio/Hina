using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Hooks;
using Hina.PackageManager.Paths;
using Hina.PackageManager.Platform;
using Hina.PackageManager.Registry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hina.PackageManager.Install
{
    // `hina uninstall <name>`. Reads side-effects from the registry (NEVER the live
    // descriptor — a newer descriptor might list different hooks). Every step is fail-soft
    // and idempotent so repeated calls converge to "clean".
    public sealed class UninstallService
    {
        private readonly InstallPaths _paths;
        private readonly IPlatformIntegration _platform;
        private readonly ILogger _logger;

        public UninstallService(InstallPaths paths, IPlatformIntegration platform, ILogger? logger = null)
        {
            _paths = paths;
            _platform = platform;
            _logger = logger ?? NullLogger.Instance;
        }

        public async Task<UninstallResult> UninstallAsync(string name, CancellationToken ct)
        {
            LockManager locks = new LockManager(_paths.LockFile);
            using RegistryLock l = await locks.AcquireAsync(ct);

            RegistryStore store = new RegistryStore(_paths.RegistryFile);
            Registry.Registry registry = store.Load();

            if (!registry.Apps.TryGetValue(name, out InstalledApp? app))
            {
                _logger.LogInformation("Uninstall: '{Name}' not in registry, nothing to do.", name);
                return new UninstallResult { Name = name, Removed = false };
            }

            HookExecutor hooks = new HookExecutor(_platform);

            // Reverse-order hook undo. Fail-soft on real errors so repeated uninstalls converge,
            // but user cancellation (Ctrl+C) must NOT be treated as a step failure: it has to
            // stop the uninstall before the destructive directory delete below.
            for (int i = app.ExecutedHooks.Count - 1; i >= 0; i--)
            {
                try { await hooks.UndoAsync(app.ExecutedHooks[i], ct); }
                catch (System.Exception ex) when (ex is not System.OperationCanceledException) { _logger.LogDebug(ex, "Undo of hook {Action} failed for {Name} (fail-soft).", app.ExecutedHooks[i].Action, name); }
            }

            foreach (ShellEntryRecord entry in app.ShellEntries)
            {
                try { await _platform.RemoveMenuShortcut(entry.Evidence, ct); }
                catch (System.Exception ex) when (ex is not System.OperationCanceledException) { _logger.LogDebug(ex, "Removal of shell entry {Id} failed for {Name} (fail-soft).", entry.Id, name); }
            }

            // Last chance to honour Ctrl+C before the point of no return: everything above is
            // re-doable, the recursive delete below is not.
            ct.ThrowIfCancellationRequested();

            // App directory: refuse to follow a symlink. If a user (or a previous Hina
            // install with --portable or similar) made InstallPath a symlink pointing at
            // their actual data dir, Directory.Delete(recursive: true) on a symlink walks
            // INTO the target and wipes its contents. We delete only the link, leaving
            // the original directory intact.
            try
            {
                if (Directory.Exists(app.InstallPath))
                {
                    DirectoryInfo info = new DirectoryInfo(app.InstallPath);
                    if (info.LinkTarget != null)
                    {
                        // It's a directory symlink. Directory.Delete(recursive:false) removes the
                        // link itself without descending into the target — and works on Windows,
                        // where File.Delete throws UnauthorizedAccessException on a directory
                        // reparse point (silently swallowed before, leaking the link).
                        Directory.Delete(app.InstallPath, recursive: false);
                    }
                    else
                    {
                        Directory.Delete(app.InstallPath, recursive: true);
                    }
                }
            }
            catch (System.Exception ex) { _logger.LogDebug(ex, "Removal of install dir {Path} failed for {Name} (fail-soft).", app.InstallPath, name); }

            // If the install dir is still there (delete failed: locked file on Windows, a
            // read-only parent, missing permission), keep the registry row. Dropping it would
            // strand the files on disk with nothing pointing at them — `verify`/`repair` only
            // detect registered apps, so the leftovers would be invisible and unrecoverable by
            // Hina. Keeping the row leaves the app listed and the uninstall retryable.
            if (Directory.Exists(app.InstallPath))
            {
                _logger.LogWarning(
                    "Uninstall: could not remove install directory {Path} for '{Name}'. The app is left registered so you can retry `hina uninstall {Name}` once the directory is free.",
                    app.InstallPath, name, name);
                return new UninstallResult { Name = name, Removed = false };
            }

            try
            {
                string descCache = _paths.DescriptorCache(name);
                if (File.Exists(descCache)) File.Delete(descCache);
            }
            catch (System.Exception ex) { _logger.LogDebug(ex, "Removal of descriptor cache failed for {Name} (fail-soft).", name); }

            // Commit point past the point of no return declared above: the install dir is
            // already gone, so this write must not observe ct — a Ctrl+C here would leave
            // a registry row pointing at a deleted directory (ghost app in `hina list`).
            registry.Apps.Remove(name);
            await store.SaveAsync(registry, CancellationToken.None);

            return new UninstallResult { Name = name, Removed = true };
        }
    }

    public sealed class UninstallResult
    {
        public string Name { get; init; } = string.Empty;
        public bool Removed { get; init; }
    }
}
