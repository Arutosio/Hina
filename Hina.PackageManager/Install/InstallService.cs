using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Configuration;
using Hina.PackageManager.Io;
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
    // End-to-end `hina install <url>` orchestration.
    // Hard rule: nothing is written to disk in user-visible places before validation +
    // signature checks pass. Once writes begin, InstallTransaction tracks them so any
    // failure rolls everything back.
    public sealed class InstallService
    {
        private readonly InstallPaths _paths;
        private readonly IPlatformIntegration _platform;
        private readonly DescriptorFetcher _fetcher;
        private readonly Func<PatcherConfig, IPatchClient> _patchClientFactory;
        private readonly ILogger _logger;

        public InstallService(
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

        public async Task<InstallResult> InstallAsync(Uri descriptorUrl, InstallOptions? options, CancellationToken ct)
        {
            options ??= new InstallOptions();

            // [1] Fetch, then install the fetched descriptor.
            _logger.LogInformation("Fetching descriptor {Url}", descriptorUrl);
            AppDescriptor descriptor = await _fetcher.FetchAsync(descriptorUrl, ct);
            return await InstallAsync(descriptor, descriptorUrl, options, ct);
        }

        // Install an ALREADY-fetched descriptor. ReinstallService uses this so the descriptor
        // it fetched, key-pinned and signature-checked is the exact one installed — a second
        // fetch here would let a malicious/compromised server swap in a different (but
        // self-signed, so still passing the checks below) descriptor after the pin check, with
        // the app already uninstalled (TOCTOU).
        public async Task<InstallResult> InstallAsync(AppDescriptor descriptor, Uri descriptorUrl, InstallOptions? options, CancellationToken ct)
        {
            options ??= new InstallOptions();

            // [2-3] Parse + validate.
            DescriptorValidator.Validate(descriptor, new ValidationContext { AllowInsecure = options.AllowInsecure }).EnsureValid();

            // [3a] Enforce descriptor.minHinaVersion against the running binary so an app
            //      requiring features from a newer Hina cannot be installed by an older one.
            if (!string.IsNullOrWhiteSpace(descriptor.MinHinaVersion) && !HinaVersion.IsSatisfiedBy(descriptor.MinHinaVersion))
            {
                throw new InvalidOperationException(
                    $"App '{descriptor.Name}' requires Hina {descriptor.MinHinaVersion} or newer; running {HinaVersion.Current}. Upgrade Hina first.");
            }

            // [4] Signature: descriptor must be signed; the same publicKey it claims must verify it.
            if (descriptor.DescriptorSignature == null)
            {
                throw new InvalidDataException("Descriptor is not signed.");
            }
            if (!DescriptorSigner.Verify(descriptor, descriptor.PublicKey))
            {
                throw new InvalidDataException("Descriptor signature does not match its declared publicKey.");
            }

            // [6-7] Lock + duplicate check.
            _paths.EnsureRootDirs();
            LockManager locks = new LockManager(_paths.LockFile);
            using RegistryLock l = await locks.AcquireAsync(ct);

            RegistryStore store = new RegistryStore(_paths.RegistryFile);
            Registry.Registry registry = store.Load();

            if (registry.Apps.TryGetValue(descriptor.Name, out InstalledApp? existing))
            {
                throw new InvalidOperationException(
                    $"'{descriptor.Name}' is already installed (version {existing.InstalledVersion}). Use `hina update` or `hina reinstall`.");
            }

            // TOFU prompt — first-time install for this app.
            if (options.OnFirstTimeTrust != null)
            {
                bool accepted = options.OnFirstTimeTrust(new TrustPrompt
                {
                    AppName = descriptor.DisplayName,
                    Publisher = descriptor.Publisher,
                    DescriptorUrl = descriptorUrl.ToString(),
                    PublicKeyFingerprint = ComputeFingerprint(descriptor.PublicKey)
                });
                if (!accepted)
                {
                    throw new OperationCanceledException("User rejected publisher's signing key.");
                }
            }
            else if (!options.AssumeTrustOnFirstUse)
            {
                // Fail-closed: no prompt and no explicit opt-in, so we won't silently pin an
                // unverified publisher key. Library consumers must either supply OnFirstTimeTrust
                // or set AssumeTrustOnFirstUse to accept the key on first use.
                throw new InvalidOperationException(
                    $"Refusing to trust publisher key for '{descriptor.Name}' on first install: no trust prompt was provided. " +
                    "Set InstallOptions.OnFirstTimeTrust to confirm interactively, or AssumeTrustOnFirstUse=true to auto-accept.");
            }

            // [8] Resolve install dir and refuse to overwrite non-empty content.
            string appDir = _paths.AppDir(descriptor.Name);
            if (Directory.Exists(appDir) && Directory.EnumerateFileSystemEntries(appDir).Any())
            {
                throw new InvalidOperationException(
                    $"Install directory '{appDir}' already exists and is not empty.");
            }

            // [9-15] Stage everything under a transaction so any failure rolls back.
            HookExecutor hooks = new HookExecutor(_platform, _logger);
            InstallTransaction tx = new InstallTransaction(_platform, hooks);
            InstalledApp newApp = new InstalledApp
            {
                Name = descriptor.Name,
                InstalledVersion = descriptor.Version,
                InstallPath = appDir,
                DescriptorUrl = descriptorUrl.ToString(),
                BaseUrl = descriptor.BaseUrl,
                Channel = descriptor.Channel,
                PublicKey = descriptor.PublicKey,
                InstalledAt = DateTimeOffset.UtcNow,
                LastUpdatedAt = DateTimeOffset.UtcNow
            };

            try
            {
                Directory.CreateDirectory(appDir);
                tx.RecordAppDirCreated(appDir);

                // [10] Delta-fetch chunks into appDir.
                PatcherConfig patchCfg = options.Network.ToPatchConfig(
                    descriptor.BaseUrl, descriptor.Channel, descriptor.PublicKey, backup: false);
                IPatchClient patcher = _patchClientFactory(patchCfg);
                PatchResult patchResult = await patcher.PatchAsync(appDir, ct);
                if (!patchResult.Success)
                {
                    throw new InvalidOperationException("PatchClient.PatchAsync reported failure.");
                }

                // [11] Sanity check: the per-OS exec exists on disk after patching.
                string? execRelative = ExecForCurrentOs(descriptor);
                if (execRelative == null)
                {
                    throw new InvalidOperationException("Descriptor has no exec entry for the current OS.");
                }
                string execAbs = Path.Combine(appDir, execRelative);
                if (!File.Exists(execAbs))
                {
                    throw new InvalidDataException($"Declared exec '{execRelative}' is missing from installed files.");
                }

                // [12] Shell entries. Sandboxed apps launch via `hina run` so the
                // filesystem sandbox is installed before the app process starts — but only Linux
                // enforces it today. On other OSes the launchOverride is ignored (the app launches
                // directly), so we must NOT route through `hina run` (it would gain nothing) and we
                // must tell the user the sandbox is not actually enforced here.
                bool sandboxRequested = descriptor.Sandbox?.Enabled == true;
                bool sandboxEnforceable = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
                if (sandboxRequested)
                {
                    DiscloseSandbox(descriptor.Sandbox!, sandboxEnforceable);
                }
                bool routeThroughHina = sandboxRequested && sandboxEnforceable;
                string hinaExe = Environment.ProcessPath ?? "hina";
                foreach (ShellEntry entry in descriptor.Entries)
                {
                    string? launchOverride = routeThroughHina
                        ? $"\"{hinaExe}\" run {descriptor.Name} \"{entry.Id}\""
                        : null;
                    string evidence = await _platform.CreateMenuShortcut(entry, appDir, launchOverride, ct);
                    tx.RecordShellEntry(evidence);
                    newApp.ShellEntries.Add(new ShellEntryRecord { Id = entry.Id, Evidence = evidence });
                }

                // [13] Post-install hooks in declared order.
                foreach (HookAction hook in descriptor.PostInstall)
                {
                    HookEvidence evidence = await hooks.ApplyAsync(hook, appDir, descriptor.Name, descriptor.Entries, ct);
                    tx.RecordHook(evidence);
                    newApp.ExecutedHooks.Add(evidence);
                }

                // [14] Cache descriptor FIRST (atomic), so the committed registry row below always
                // has a readable descriptor — `run`/`perms`/`update` depend on it. A crash between
                // the two writes then leaves a cache with no registry row (harmless) rather than a
                // registry row with no cache (breaks run/perms).
                AtomicFile.WriteAllText(_paths.DescriptorCache(descriptor.Name), DescriptorParser.Serialize(descriptor));

                // [15] Commit registry entry.
                registry.Apps[descriptor.Name] = newApp;
                await store.SaveAsync(registry, ct);
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }

            return new InstallResult
            {
                Name = descriptor.Name,
                Version = descriptor.Version,
                InstallPath = appDir
            };
        }

        // Tell the user exactly what filesystem scope a sandboxed app declares, with extra
        // emphasis on the unrestricted "host" escape hatch. `enforceable` is false on OSes whose
        // sandbox backend isn't implemented yet (macOS/Windows) — there the scope is declared but
        // NOT applied, so we must say so plainly instead of implying isolation that won't happen.
        private void DiscloseSandbox(SandboxSpec sandbox, bool enforceable)
        {
            if (!enforceable)
            {
                _logger.LogWarning(
                    "This app DECLARES a filesystem sandbox, but Hina does not enforce sandboxing on this OS yet. " +
                    "The app will run with FULL user privileges (no isolation). Its declared scope:");
            }
            else
            {
                _logger.LogInformation("This app runs sandboxed. It can access only:");
            }
            _logger.LogInformation("  - its own install directory (read-only)");
            foreach (FsRule rule in sandbox.Filesystem)
            {
                if (rule.Path == SandboxTokens.Host)
                {
                    _logger.LogWarning("  - host: UNRESTRICTED filesystem access ({Access}) — this app is NOT isolated.", rule.Access);
                }
                else
                {
                    _logger.LogInformation("  - {Path} ({Access})", rule.Path, rule.Access);
                }
            }
            if (enforceable)
            {
                _logger.LogInformation("Grant more paths later with: hina perms {Name} --grant <path>[:rw]", "<app>");
            }
        }

        public static string? ExecForCurrentOs(AppDescriptor descriptor)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return descriptor.Exec.Linux;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return descriptor.Exec.Macos;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return descriptor.Exec.Windows;
            return null;
        }

        // Short, human-readable fingerprint (first 16 hex chars of SHA-256 of the base64 key bytes).
        // Good enough for "compare against publisher's site" — full key is also shown.
        public static string ComputeFingerprint(string publicKeyBase64)
        {
            byte[] raw;
            try { raw = Convert.FromBase64String(publicKeyBase64); }
            catch (FormatException) { return "<invalid>"; }

            byte[] hash = SHA256.HashData(raw);
            StringBuilder sb = new StringBuilder(23);
            for (int i = 0; i < 8; i++)
            {
                if (i > 0 && i % 2 == 0) sb.Append(':');
                sb.Append(hash[i].ToString("x2"));
            }
            return sb.ToString();
        }
    }

    public sealed class InstallResult
    {
        public string Name { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public string InstallPath { get; init; } = string.Empty;
    }
}
