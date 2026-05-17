using System;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Configuration;
using Hina.Core.Patching;
using Hina.PackageManager.Paths;
using Hina.PackageManager.Platform;
using Hina.PackageManager.Registry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hina.PackageManager.Install
{
    // `hina reinstall <name>`. Reads the descriptor URL from the registry, uninstalls
    // cleanly, then re-runs InstallService against the same URL. Use --rotate-key to
    // accept a publisher key change (otherwise the TOFU lock from the install flow
    // would reject any descriptor signed with a new key).
    public sealed class ReinstallService
    {
        private readonly InstallPaths _paths;
        private readonly IPlatformIntegration _platform;
        private readonly Descriptor.DescriptorFetcher _fetcher;
        private readonly Func<PatcherConfig, IPatchClient> _patchClientFactory;
        private readonly ILogger _logger;

        public ReinstallService(
            InstallPaths paths,
            IPlatformIntegration platform,
            Descriptor.DescriptorFetcher? fetcher = null,
            Func<PatcherConfig, IPatchClient>? patchClientFactory = null,
            ILogger? logger = null)
        {
            _paths = paths;
            _platform = platform;
            _fetcher = fetcher ?? new Descriptor.DescriptorFetcher();
            _patchClientFactory = patchClientFactory ?? (cfg => new PatchClient(cfg));
            _logger = logger ?? NullLogger.Instance;
        }

        public async Task<InstallResult> ReinstallAsync(string name, bool rotateKey, CancellationToken ct)
        {
            // Read URL inside a short lock; uninstall/install each take their own.
            string descriptorUrl;
            {
                LockManager locks = new LockManager(_paths.LockFile);
                using RegistryLock l = await locks.AcquireAsync(ct);
                Registry.Registry registry = new RegistryStore(_paths.RegistryFile).Load();
                if (!registry.Apps.TryGetValue(name, out InstalledApp? app))
                {
                    throw new InvalidOperationException($"'{name}' is not installed; use `hina install <url>` instead.");
                }
                descriptorUrl = app.DescriptorUrl;
            }

            UninstallService uninstall = new UninstallService(_paths, _platform, _logger);
            await uninstall.UninstallAsync(name, ct);

            InstallService install = new InstallService(_paths, _platform, _fetcher, _patchClientFactory, _logger);

            // For rotate-key flows we silently accept whatever publicKey the new descriptor
            // declares; otherwise we let the normal first-time-trust path run (which, because
            // there is now no registry entry, behaves as a fresh install).
            InstallOptions opts = new InstallOptions
            {
                OnFirstTimeTrust = rotateKey ? _ => true : null
            };
            return await install.InstallAsync(new Uri(descriptorUrl), opts, ct);
        }
    }
}
