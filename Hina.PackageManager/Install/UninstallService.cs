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

            // Reverse-order hook undo.
            for (int i = app.ExecutedHooks.Count - 1; i >= 0; i--)
            {
                try { await hooks.UndoAsync(app.ExecutedHooks[i], ct); }
                catch { /* fail-soft */ }
            }

            foreach (ShellEntryRecord entry in app.ShellEntries)
            {
                try { await _platform.RemoveMenuShortcut(entry.Evidence, ct); }
                catch { /* fail-soft */ }
            }

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
                        // It's a symlink — File.Delete removes the link without touching the target.
                        File.Delete(app.InstallPath);
                    }
                    else
                    {
                        Directory.Delete(app.InstallPath, recursive: true);
                    }
                }
            }
            catch { /* fail-soft */ }

            try
            {
                string descCache = _paths.DescriptorCache(name);
                if (File.Exists(descCache)) File.Delete(descCache);
            }
            catch { /* fail-soft */ }

            registry.Apps.Remove(name);
            await store.SaveAsync(registry, ct);

            return new UninstallResult { Name = name, Removed = true };
        }
    }

    public sealed class UninstallResult
    {
        public string Name { get; init; } = string.Empty;
        public bool Removed { get; init; }
    }
}
