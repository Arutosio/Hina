using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Diagnostics;
using Hina.PackageManager.Install;
using Hina.PackageManager.Paths;
using Hina.PackageManager.Platform;
using Hina.PackageManager.Registry;
using Microsoft.Extensions.Logging;

namespace Hina.CLI
{
    // Composition root for the CLI. Builds InstallPaths, the platform integration, and the
    // package-manager services in one place so commands don't each repeat the wiring (a
    // constructor signature change now touches this file only). The platform integration is
    // lazily created — read-only commands (list/info/which) never instantiate it.
    //
    // No DI container: NativeAOT prefers static composition over reflection-based resolution.
    internal sealed class CommandContext
    {
        public InstallPaths Paths { get; }
        public ILogger Logger { get; }
        public ILoggerFactory LoggerFactory { get; }
        public CancellationToken Ct { get; }

        private IPlatformIntegration? _platform;
        public IPlatformIntegration Platform => _platform ??= PlatformIntegrationFactory.Current(Paths, Logger);

        public CommandContext(InstallPaths paths, ILogger logger, ILoggerFactory loggerFactory, CancellationToken ct)
        {
            Paths = paths;
            Logger = logger;
            LoggerFactory = loggerFactory;
            Ct = ct;
        }

        public static CommandContext ForCurrentOs(ILogger logger, ILoggerFactory loggerFactory, CancellationToken ct)
            => new CommandContext(InstallPaths.ForCurrentOs(), logger, loggerFactory, ct);

        // Service factories — single source of construction for the package-manager services.
        public InstallService NewInstallService() => new InstallService(Paths, Platform, logger: Logger);
        public UninstallService NewUninstallService() => new UninstallService(Paths, Platform, Logger);
        public UpdateService NewUpdateService() => new UpdateService(Paths, Platform, logger: Logger);
        public ReinstallService NewReinstallService() => new ReinstallService(Paths, Platform, logger: Logger);
        public RegistryVerifier NewRegistryVerifier() => new RegistryVerifier(Paths, Platform, Logger);

        public RegistryStore NewRegistryStore() => new RegistryStore(Paths.RegistryFile, Logger);
        public LockManager NewLockManager() => new LockManager(Paths.LockFile);

        // #7: read-only commands must read the registry under the same exclusive lock the
        // writers hold, otherwise `hina list` can observe a half-written registry while an
        // install/update is mid-commit. The lock window is brief — just the Load().
        public async Task<Registry> LoadRegistryLockedAsync()
        {
            LockManager locks = NewLockManager();
            using RegistryLock l = await locks.AcquireAsync(Ct);
            return await NewRegistryStore().LoadAsync(Ct);
        }
    }
}
