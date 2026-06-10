using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Configuration;
using Hina.Core.Patching;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Paths;
using Hina.PackageManager.Platform;
using Hina.PackageManager.Registry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hina.PackageManager.Install
{
    // `hina reinstall <name>`. Re-fetches the descriptor from the URL captured at install
    // time, uninstalls cleanly, then re-runs InstallService against the same URL.
    //
    // Without --rotate-key, ReinstallService refuses if the new descriptor declares a
    // different publisher key from the one currently pinned in the registry; this prevents
    // a silent key-rotation from sneaking through a reinstall (which would otherwise
    // bypass UpdateService's pin check). The fetch + key check happen BEFORE uninstall
    // so a refusal leaves the registry untouched.
    public sealed class ReinstallService
    {
        private readonly InstallPaths _paths;
        private readonly IPlatformIntegration _platform;
        private readonly DescriptorFetcher _fetcher;
        private readonly Func<PatcherConfig, IPatchClient> _patchClientFactory;
        private readonly ILogger _logger;

        public ReinstallService(
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

        public async Task<InstallResult> ReinstallAsync(string name, bool rotateKey, CancellationToken ct)
        {
            // [1] Read existing registry entry to learn descriptorUrl + pinned key.
            string descriptorUrl;
            string pinnedKey;
            {
                LockManager locks = new LockManager(_paths.LockFile);
                using RegistryLock l = await locks.AcquireAsync(ct);
                Registry.Registry registry = new RegistryStore(_paths.RegistryFile).Load();
                if (!registry.Apps.TryGetValue(name, out InstalledApp? app))
                {
                    throw new InvalidOperationException($"'{name}' is not installed; use `hina install <url>` instead.");
                }
                descriptorUrl = app.DescriptorUrl;
                pinnedKey = app.PublicKey;
            }

            // [2] Fetch the new descriptor and enforce the key-rotation rule BEFORE we
            //     touch anything on disk. If --rotate-key isn't set and the publisher
            //     rotated, abort — the user must re-issue with --rotate-key.
            AppDescriptor descriptor = await _fetcher.FetchAsync(new Uri(descriptorUrl), ct);
            if (!rotateKey && !string.Equals(descriptor.PublicKey, pinnedKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Descriptor declares a different publisher key. Re-run with `--rotate-key` if you trust the new key.");
            }

            // [2a] Verify the signature BEFORE uninstalling. The key-equality check above only
            //      compares the declared publicKey field — a forged descriptor with the right
            //      field but an invalid signature would otherwise pass, get the app uninstalled,
            //      and only fail at the re-install signature check, leaving the user with nothing.
            string verifyingKey = rotateKey ? descriptor.PublicKey : pinnedKey;
            if (!DescriptorSigner.Verify(descriptor, verifyingKey))
            {
                throw new InvalidOperationException(
                    "Descriptor signature is invalid; refusing to reinstall. The installed app is left untouched.");
            }

            // [2b] Validate the descriptor and enforce minHinaVersion BEFORE uninstalling, for
            //      the same reason as the signature check above: InstallService re-runs both,
            //      but by then the app is already gone — a publisher who shipped a broken
            //      descriptor or bumped minHinaVersion would strand the user app-less.
            DescriptorValidator.Validate(descriptor).EnsureValid();
            if (!string.IsNullOrWhiteSpace(descriptor.MinHinaVersion) && !HinaVersion.IsSatisfiedBy(descriptor.MinHinaVersion))
            {
                throw new InvalidOperationException(
                    $"App '{descriptor.Name}' requires Hina {descriptor.MinHinaVersion} or newer; running {HinaVersion.Current}. " +
                    "Upgrade Hina first, then re-run the reinstall. The installed app is left untouched.");
            }

            // [3] Uninstall.
            UninstallService uninstall = new UninstallService(_paths, _platform, _logger);
            await uninstall.UninstallAsync(name, ct);

            // [4] Install the SAME descriptor we just fetched and verified. Passing it in
            //     (rather than letting InstallService re-fetch) closes the TOCTOU window: the
            //     app is already uninstalled here, so a swapped descriptor on a second fetch
            //     would install attacker-chosen content under the user's trust.
            InstallService install = new InstallService(_paths, _platform, _fetcher, _patchClientFactory, _logger);
            InstallOptions opts = new InstallOptions { OnFirstTimeTrust = _ => true };
            return await install.InstallAsync(descriptor, new Uri(descriptorUrl), opts, ct);
        }
    }
}
