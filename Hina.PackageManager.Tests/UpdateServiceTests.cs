using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Crypto;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Install;
using Hina.PackageManager.Paths;
using Hina.PackageManager.Registry;

namespace Hina.PackageManager.Tests
{
    public class UpdateServiceTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly InstallPaths _paths;
        private readonly FakePlatformIntegration _platform;
        private readonly string _privKey;
        private readonly string _pubKey;

        public UpdateServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "hina-update-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_tempDir);
            _paths = InstallPaths.ForRoot(_tempDir);
            _platform = new FakePlatformIntegration();
            (_privKey, _pubKey) = KeyGenerator.GenerateEd25519();
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task Update_BumpsVersionAndRefreshesRegistry()
        {
            await InstallV1();

            AppDescriptor v2 = BuildDescriptor(_pubKey, version: "1.1.0");
            DescriptorSigner.AttachSignature(v2, Convert.FromBase64String(_privKey));

            UpdateService svc = new UpdateService(
                _paths,
                _platform,
                fetcher: new StubFetcher(v2),
                patchClientFactory: cfg => new FakePatchClient(cfg, NewExecFiles()));

            UpdateResult result = await svc.UpdateAsync("demo", null, CancellationToken.None);

            Assert.Equal(UpdateStatus.Updated, result.Status);
            Assert.Equal("1.0.0", result.FromVersion);
            Assert.Equal("1.1.0", result.ToVersion);

            Registry.Registry reg = new RegistryStore(_paths.RegistryFile).Load();
            Assert.Equal("1.1.0", reg.Apps["demo"].InstalledVersion);
        }

        [Fact]
        public async Task Update_OlderVersion_IsRefusedAsDowngrade()
        {
            await InstallV1(); // 1.0.0

            AppDescriptor older = BuildDescriptor(_pubKey, version: "0.9.0");
            DescriptorSigner.AttachSignature(older, Convert.FromBase64String(_privKey));

            UpdateService svc = new UpdateService(
                _paths, _platform,
                fetcher: new StubFetcher(older),
                patchClientFactory: cfg => new FakePatchClient(cfg, NewExecFiles()));

            UpdateResult result = await svc.UpdateAsync("demo", null, CancellationToken.None);

            Assert.Equal(UpdateStatus.Failed, result.Status);
            Assert.Contains("downgrade", result.Message, StringComparison.OrdinalIgnoreCase);
            Registry.Registry reg = new RegistryStore(_paths.RegistryFile).Load();
            Assert.Equal("1.0.0", reg.Apps["demo"].InstalledVersion);
        }

        [Fact]
        public async Task Update_OlderVersion_WithAllowDowngrade_Proceeds()
        {
            await InstallV1(); // 1.0.0

            AppDescriptor older = BuildDescriptor(_pubKey, version: "0.9.0");
            DescriptorSigner.AttachSignature(older, Convert.FromBase64String(_privKey));

            UpdateService svc = new UpdateService(
                _paths, _platform,
                fetcher: new StubFetcher(older),
                patchClientFactory: cfg => new FakePatchClient(cfg, NewExecFiles()));

            UpdateResult result = await svc.UpdateAsync("demo", new UpdateOptions { AllowDowngrade = true }, CancellationToken.None);

            Assert.Equal(UpdateStatus.Updated, result.Status);
            Registry.Registry reg = new RegistryStore(_paths.RegistryFile).Load();
            Assert.Equal("0.9.0", reg.Apps["demo"].InstalledVersion);
        }

        [Fact]
        public async Task Update_SameVersion_ReturnsAlreadyUpToDate()
        {
            await InstallV1();

            AppDescriptor sameVersion = BuildDescriptor(_pubKey, version: "1.0.0");
            DescriptorSigner.AttachSignature(sameVersion, Convert.FromBase64String(_privKey));

            UpdateService svc = new UpdateService(
                _paths,
                _platform,
                fetcher: new StubFetcher(sameVersion),
                patchClientFactory: cfg => new FakePatchClient(cfg, NewExecFiles()));

            UpdateResult result = await svc.UpdateAsync("demo", null, CancellationToken.None);

            Assert.Equal(UpdateStatus.AlreadyUpToDate, result.Status);
        }

        [Fact]
        public async Task Update_KeyRotationWithoutFlag_IsRejected()
        {
            await InstallV1();

            (string otherPriv, string otherPub) = KeyGenerator.GenerateEd25519();
            AppDescriptor rotated = BuildDescriptor(otherPub, version: "1.1.0");
            DescriptorSigner.AttachSignature(rotated, Convert.FromBase64String(otherPriv));

            UpdateService svc = new UpdateService(
                _paths,
                _platform,
                fetcher: new StubFetcher(rotated),
                patchClientFactory: cfg => new FakePatchClient(cfg, NewExecFiles()));

            UpdateResult result = await svc.UpdateAsync("demo", null, CancellationToken.None);

            Assert.Equal(UpdateStatus.Failed, result.Status);
            Assert.Contains("pinned", result.Message);

            // Registry still on v1, pinned key unchanged.
            Registry.Registry reg = new RegistryStore(_paths.RegistryFile).Load();
            Assert.Equal("1.0.0", reg.Apps["demo"].InstalledVersion);
            Assert.Equal(_pubKey, reg.Apps["demo"].PublicKey);
        }

        [Fact]
        public async Task Update_AddsNewHookAndRemovesOldHook()
        {
            await InstallV1();

            AppDescriptor v2 = BuildDescriptor(_pubKey, version: "1.1.0");
            // v1 had addToPath:demo + a font hook; v2 drops the font hook and adds an addToPath:demo-cli.
            v2.PostInstall = new()
            {
                new AddToPathHook { Name = "demo", Target = ExecRelative() },
                new AddToPathHook { Name = "demo-cli", Target = ExecRelative() }
            };
            DescriptorSigner.AttachSignature(v2, Convert.FromBase64String(_privKey));

            UpdateService svc = new UpdateService(
                _paths,
                _platform,
                fetcher: new StubFetcher(v2),
                patchClientFactory: cfg => new FakePatchClient(cfg, NewExecFiles()));

            UpdateResult result = await svc.UpdateAsync("demo", null, CancellationToken.None);

            Assert.Equal(UpdateStatus.Updated, result.Status);

            // FakePlatformIntegration captured the new addToPath call.
            Assert.Contains("/fake/bin/demo-cli", _platform.AddedToPath);

            // The old font hook was undone.
            Assert.NotEmpty(_platform.UninstalledFonts);

            // Registry now has only the two addToPath hooks (font hook removed).
            Registry.Registry reg = new RegistryStore(_paths.RegistryFile).Load();
            InstalledApp updated = reg.Apps["demo"];
            Assert.Equal(2, updated.ExecutedHooks.Count);
            foreach (HookEvidence ev in updated.ExecutedHooks)
            {
                Assert.Equal("addToPath", ev.Action);
            }
        }

        [Fact]
        public async Task UpdateAll_IteratesEveryInstalledApp()
        {
            await InstallV1("demo");
            await InstallV1("other");

            AppDescriptor demoV2 = BuildDescriptor(_pubKey, name: "demo", version: "1.1.0");
            AppDescriptor otherV2 = BuildDescriptor(_pubKey, name: "other", version: "2.0.0");
            DescriptorSigner.AttachSignature(demoV2, Convert.FromBase64String(_privKey));
            DescriptorSigner.AttachSignature(otherV2, Convert.FromBase64String(_privKey));

            UpdateService svc = new UpdateService(
                _paths,
                _platform,
                fetcher: new MultiStubFetcher(new Dictionary<string, AppDescriptor>
                {
                    ["https://example.com/demo.json"] = demoV2,
                    ["https://example.com/other.json"] = otherV2,
                }),
                patchClientFactory: cfg => new FakePatchClient(cfg, NewExecFiles()));

            List<UpdateResult> results = await svc.UpdateAllAsync(null, CancellationToken.None);

            Assert.Equal(2, results.Count);
            foreach (UpdateResult r in results)
            {
                Assert.Equal(UpdateStatus.Updated, r.Status);
            }
        }

        [Fact]
        public async Task Update_AddPhaseFails_RollsBackAndRestoresRemovedEntry()
        {
            await InstallV1();

            // v2 drops the "main" shell entry and adds a "broken" one whose creation fails.
            AppDescriptor v2 = BuildDescriptor(_pubKey, version: "1.1.0");
            v2.Entries = new() { new ShellEntry { Id = "broken", Name = "broken", Exec = ExecRelative() } };
            DescriptorSigner.AttachSignature(v2, Convert.FromBase64String(_privKey));

            _platform.ThrowOnCreateShortcutId = "broken";

            UpdateService svc = new UpdateService(
                _paths,
                _platform,
                fetcher: new StubFetcher(v2),
                patchClientFactory: cfg => new FakePatchClient(cfg, NewExecFiles()));

            UpdateResult result = await svc.UpdateAsync("demo", null, CancellationToken.None);

            Assert.Equal(UpdateStatus.Failed, result.Status);

            // Registry rolled back to the pre-update snapshot.
            Registry.Registry reg = new RegistryStore(_paths.RegistryFile).Load();
            Assert.Equal("1.0.0", reg.Apps["demo"].InstalledVersion);

            // The "main" entry removed during the update was re-created on rollback (sourced
            // from the previous cached descriptor): created once at install, once on rollback.
            int mainCreations = _platform.CreatedShortcuts.FindAll(s => s == "/fake/apps/main.desktop").Count;
            Assert.Equal(2, mainCreations);
        }

        [Fact]
        public async Task Update_UnknownApp_ReturnsFailedWithMessage()
        {
            UpdateService svc = new UpdateService(_paths, _platform);

            UpdateResult result = await svc.UpdateAsync("ghost", null, CancellationToken.None);

            Assert.Equal(UpdateStatus.Failed, result.Status);
            Assert.Contains("not installed", result.Message);
        }

        [Fact]
        public async Task Update_PatchFailureRollback_DoesNotClobberConcurrentRegistryWrite()
        {
            await InstallV1("demo");

            AppDescriptor v2 = BuildDescriptor(_pubKey, name: "demo", version: "1.1.0");
            DescriptorSigner.AttachSignature(v2, Convert.FromBase64String(_privKey));

            RegistryStore store = new RegistryStore(_paths.RegistryFile);

            // The patcher simulates a concurrent UpdateAllAsync worker writing a sibling row
            // (lock-free patch window), then fails — triggering demo's patch-failure rollback.
            // The rollback must re-read the registry and preserve the sibling row, not overwrite
            // with the stale snapshot loaded before the patch.
            Hina.Core.Patching.IPatchClient FailingPatcher(Hina.Core.Configuration.PatcherConfig cfg) =>
                new ConcurrentWriterThenFailPatchClient(cfg, () =>
                {
                    Registry.Registry reg = store.Load();
                    reg.Apps["ghost"] = new InstalledApp
                    {
                        Name = "ghost",
                        InstalledVersion = "9.9.9",
                        InstallPath = Path.Combine(_tempDir, "ghost"),
                        DescriptorUrl = "https://example.com/ghost.json",
                        BaseUrl = "https://cdn.example.com/ghost/",
                        Channel = "stable",
                        PublicKey = _pubKey
                    };
                    store.SaveAsync(reg).GetAwaiter().GetResult();
                });

            UpdateService svc = new UpdateService(_paths, _platform, fetcher: new StubFetcher(v2), patchClientFactory: FailingPatcher);
            UpdateResult result = await svc.UpdateAsync("demo", null, CancellationToken.None);

            Assert.Equal(UpdateStatus.Failed, result.Status);
            Registry.Registry final = store.Load();
            Assert.True(final.Apps.ContainsKey("ghost"), "Concurrent sibling row was clobbered by the rollback save.");
            Assert.Equal("9.9.9", final.Apps["ghost"].InstalledVersion);
            Assert.Equal("1.0.0", final.Apps["demo"].InstalledVersion); // demo rolled back to v1
        }

        [Fact]
        public async Task UpdateAll_OneInvalidDescriptor_DoesNotAbortTheRun()
        {
            await InstallV1("alpha");
            await InstallV1("beta");

            // alpha's publisher now serves a descriptor that FAILS validation (rooted exec).
            // EnsureValid throwing out of UpdateAsync used to fault its task and abort the
            // whole UpdateAllAsync run, discarding beta's (successful) result.
            AppDescriptor bad = BuildDescriptor(_pubKey, name: "alpha", version: "1.1.0");
            bad.Exec = new ExecMap { Windows = "C:\\abs\\demo.exe", Linux = "/abs/demo", Macos = "/abs/demo" };
            DescriptorSigner.AttachSignature(bad, Convert.FromBase64String(_privKey));

            AppDescriptor good = BuildDescriptor(_pubKey, name: "beta", version: "1.1.0");
            DescriptorSigner.AttachSignature(good, Convert.FromBase64String(_privKey));

            UpdateService svc = new UpdateService(
                _paths,
                _platform,
                fetcher: new RoutingFetcher(url => url.AbsoluteUri.Contains("alpha") ? bad : good),
                patchClientFactory: cfg => new FakePatchClient(cfg, NewExecFiles()));

            List<UpdateResult> results = await svc.UpdateAllAsync(null, CancellationToken.None);

            Assert.Equal(2, results.Count);
            UpdateResult? alpha = results.Find(r => r?.Name == "alpha");
            UpdateResult? beta = results.Find(r => r?.Name == "beta");
            Assert.NotNull(alpha);
            Assert.NotNull(beta);
            Assert.Equal(UpdateStatus.Failed, alpha!.Status);
            Assert.False(string.IsNullOrWhiteSpace(alpha.Message));
            Assert.Equal(UpdateStatus.Updated, beta!.Status);
        }

        // Performs a clean v1 install via the real InstallService so the registry has a
        // realistic starting state with hooks + shell entries the update can diff against.
        private async Task InstallV1(string name = "demo")
        {
            AppDescriptor v1 = BuildDescriptor(_pubKey, name: name, version: "1.0.0");
            // Include a font hook so update tests can verify removal-by-diff.
            v1.PostInstall.Add(new InstallFontHook { Files = { "fonts/A.ttf" } });
            DescriptorSigner.AttachSignature(v1, Convert.FromBase64String(_privKey));

            InstallService install = new InstallService(
                _paths,
                _platform,
                fetcher: new StubFetcher(v1),
                patchClientFactory: cfg => new FakePatchClient(cfg, NewExecFiles()));

            string descriptorUrl = name == "demo"
                ? "https://example.com/demo.json"
                : $"https://example.com/{name}.json";
            await install.InstallAsync(new Uri(descriptorUrl), new InstallOptions { AssumeTrustOnFirstUse = true }, CancellationToken.None);
        }

        private async Task InstallSandboxedV1(SandboxSpec sandbox)
        {
            AppDescriptor v1 = BuildDescriptor(_pubKey, version: "1.0.0");
            v1.Sandbox = sandbox;
            DescriptorSigner.AttachSignature(v1, Convert.FromBase64String(_privKey));
            InstallService install = new InstallService(
                _paths, _platform,
                fetcher: new StubFetcher(v1),
                patchClientFactory: cfg => new FakePatchClient(cfg, NewExecFiles()));
            await install.InstallAsync(new Uri("https://example.com/demo.json"),
                new InstallOptions { AssumeTrustOnFirstUse = true }, CancellationToken.None);
        }

        private async Task<UpdateResult> UpdateToSandboxed(SandboxSpec? sandbox, bool acceptNewPerms)
        {
            AppDescriptor v2 = BuildDescriptor(_pubKey, version: "1.1.0");
            v2.Sandbox = sandbox;
            DescriptorSigner.AttachSignature(v2, Convert.FromBase64String(_privKey));
            UpdateService svc = new UpdateService(
                _paths, _platform,
                fetcher: new StubFetcher(v2),
                patchClientFactory: cfg => new FakePatchClient(cfg, NewExecFiles()));
            return await svc.UpdateAsync("demo",
                new UpdateOptions { AcceptNewPermissions = acceptNewPerms }, CancellationToken.None);
        }

        private static SandboxSpec Sandbox(string path, string access) => new SandboxSpec
        {
            Enabled = true,
            Filesystem = { new FsRule { Path = path, Access = access } },
        };

        [Fact]
        public async Task Update_BroaderPermissions_WithoutConsent_IsRefused()
        {
            await InstallSandboxedV1(Sandbox("home", "ro"));

            UpdateResult result = await UpdateToSandboxed(Sandbox("home", "rw"), acceptNewPerms: false);

            Assert.Equal(UpdateStatus.Failed, result.Status);
            Assert.Contains("permission", result.Message.ToLowerInvariant());
            Registry.Registry reg = new RegistryStore(_paths.RegistryFile).Load();
            Assert.Equal("1.0.0", reg.Apps["demo"].InstalledVersion);
        }

        [Fact]
        public async Task Update_BroaderPermissions_WithConsent_Proceeds()
        {
            await InstallSandboxedV1(Sandbox("home", "ro"));

            UpdateResult result = await UpdateToSandboxed(Sandbox("home", "rw"), acceptNewPerms: true);

            Assert.Equal(UpdateStatus.Updated, result.Status);
            Registry.Registry reg = new RegistryStore(_paths.RegistryFile).Load();
            Assert.Equal("1.1.0", reg.Apps["demo"].InstalledVersion);
        }

        [Fact]
        public async Task Update_NarrowerPermissions_ProceedsWithoutConsent()
        {
            await InstallSandboxedV1(Sandbox("home", "rw"));

            UpdateResult result = await UpdateToSandboxed(Sandbox("home", "ro"), acceptNewPerms: false);

            Assert.Equal(UpdateStatus.Updated, result.Status);
        }

        // BUG-043: a shell entry ADDED by an update of a sandboxed app must be routed through
        // `hina run` (the sandbox chokepoint), exactly like an install-time entry. Before the
        // fix UpdateService called the 3-arg CreateMenuShortcut (launchOverride=null), so the
        // new entry launched the binary directly, bypassing the sandbox.
        [Fact]
        public async Task Update_AddsEntryToSandboxedApp_RoutesNewEntryThroughHinaRun()
        {
            await InstallSandboxedV1(Sandbox("home", "ro"));

            // v2 keeps the same sandbox (no broadening -> no consent) but adds a new "extra" entry.
            AppDescriptor v2 = BuildDescriptor(_pubKey, version: "1.1.0");
            v2.Sandbox = Sandbox("home", "ro");
            v2.Entries = new()
            {
                new ShellEntry { Id = "main", Name = "demo", Exec = ExecRelative() },
                new ShellEntry { Id = "extra", Name = "extra", Exec = ExecRelative() },
            };
            DescriptorSigner.AttachSignature(v2, Convert.FromBase64String(_privKey));

            UpdateService svc = new UpdateService(
                _paths, _platform,
                fetcher: new StubFetcher(v2),
                patchClientFactory: cfg => new FakePatchClient(cfg, NewExecFiles()));
            UpdateResult result = await svc.UpdateAsync("demo",
                new UpdateOptions { AcceptNewPermissions = false }, CancellationToken.None);

            Assert.Equal(UpdateStatus.Updated, result.Status);

            // The added entry must have received a non-null `hina run` launch override.
            (string Id, string? LaunchOverride) added =
                Assert.Single(_platform.CreatedWithOverride, c => c.Id == "extra");
            Assert.NotNull(added.LaunchOverride);
            Assert.Contains("run demo", added.LaunchOverride);
        }

        // BUG-006: when the cached previous descriptor is missing (e.g. deleted by the user),
        // the sandbox gate must NOT fail-open for the dangerous case: new version declares no
        // active sandbox (sandbox dropped or never declared). Without a baseline we cannot tell
        // whether a previously-sandboxed install is losing its isolation, so consent is required.
        [Fact]
        public async Task Update_DroppedSandbox_NoCachedDescriptor_WithoutConsent_IsRefused()
        {
            // Install with sandbox enabled so v1 was sandboxed; delete the cache to simulate
            // a missing/corrupt baseline before running the update.
            await InstallSandboxedV1(Sandbox("home", "ro"));
            string cachePath = _paths.DescriptorCache("demo");
            if (File.Exists(cachePath)) File.Delete(cachePath);

            // v2 has no sandbox (null) — without a baseline, the gate cannot confirm old was
            // also unsandboxed, so it must block (fail-closed).
            UpdateResult result = await UpdateToSandboxed(sandbox: null, acceptNewPerms: false);

            Assert.Equal(UpdateStatus.Failed, result.Status);
            Assert.Contains("missing or unreadable", result.Message, StringComparison.OrdinalIgnoreCase);

            // Registry must remain at v1.
            Registry.Registry reg = new RegistryStore(_paths.RegistryFile).Load();
            Assert.Equal("1.0.0", reg.Apps["demo"].InstalledVersion);
        }

        // BUG-006: same scenario but with --accept-new-permissions: the update must proceed
        // even when the cache is absent, because the user has explicitly consented.
        [Fact]
        public async Task Update_DroppedSandbox_NoCachedDescriptor_WithConsent_Proceeds()
        {
            await InstallSandboxedV1(Sandbox("home", "ro"));
            string cachePath = _paths.DescriptorCache("demo");
            if (File.Exists(cachePath)) File.Delete(cachePath);

            // v2 drops the sandbox — user has consented, so it must proceed.
            UpdateResult result = await UpdateToSandboxed(sandbox: null, acceptNewPerms: true);

            Assert.Equal(UpdateStatus.Updated, result.Status);
            Registry.Registry reg = new RegistryStore(_paths.RegistryFile).Load();
            Assert.Equal("1.1.0", reg.Apps["demo"].InstalledVersion);
        }

        // Residual BUG-006 hole (found verifying the "DA CONFERMARE" list): the earlier fix
        // only failed closed when the NEW version had no sandbox, assuming "sandbox on = always
        // narrowing". That is false: a narrow sandbox can be replaced by a broader one. With the
        // baseline cache missing, SandboxDiff.Compute(null, broadSpec) treats old as unsandboxed
        // and reports Broadened=false, so a real broadening (home/ro -> home/rw) slipped through
        // without consent. The gate now fails closed for ANY missing baseline.
        [Fact]
        public async Task Update_BroadenedSandbox_NoCachedDescriptor_WithoutConsent_IsRefused()
        {
            await InstallSandboxedV1(Sandbox("home", "ro"));
            string cachePath = _paths.DescriptorCache("demo");
            if (File.Exists(cachePath)) File.Delete(cachePath);

            // v2 keeps a sandbox but widens it (ro -> rw). Without the baseline the diff cannot
            // see the broadening, so the gate must block rather than fail open.
            UpdateResult result = await UpdateToSandboxed(Sandbox("home", "rw"), acceptNewPerms: false);

            Assert.Equal(UpdateStatus.Failed, result.Status);
            Assert.Contains("missing or unreadable", result.Message, StringComparison.OrdinalIgnoreCase);

            Registry.Registry reg = new RegistryStore(_paths.RegistryFile).Load();
            Assert.Equal("1.0.0", reg.Apps["demo"].InstalledVersion);
        }

        // Same scenario but with explicit consent: the update must proceed.
        [Fact]
        public async Task Update_BroadenedSandbox_NoCachedDescriptor_WithConsent_Proceeds()
        {
            await InstallSandboxedV1(Sandbox("home", "ro"));
            string cachePath = _paths.DescriptorCache("demo");
            if (File.Exists(cachePath)) File.Delete(cachePath);

            UpdateResult result = await UpdateToSandboxed(Sandbox("home", "rw"), acceptNewPerms: true);

            Assert.Equal(UpdateStatus.Updated, result.Status);
            Registry.Registry reg = new RegistryStore(_paths.RegistryFile).Load();
            Assert.Equal("1.1.0", reg.Apps["demo"].InstalledVersion);
        }

        // BUG-014: when store.Load() throws inside the final registry-write try block,
        // the recovery snapshot must still contain the v2 (updated) row — not the stale
        // pre-update registry that was in scope before Load() was called.
        [Fact]
        public async Task Update_RegistrySaveFails_RecoverySnapshotContainsUpdatedRow()
        {
            await InstallV1();

            AppDescriptor v2 = BuildDescriptor(_pubKey, version: "1.1.0");
            DescriptorSigner.AttachSignature(v2, Convert.FromBase64String(_privKey));

            string registryPath = _paths.RegistryFile;
            string recoveryPath = registryPath + ".recovery.json";

            // Strategy: after the patch succeeds, corrupt registry.json with invalid JSON
            // so that store.Load() inside the commit try throws RegistryCorruptException.
            // Also block SaveAsync by creating a directory at the .tmp path it writes to
            // (FileStream(FileMode.Create) on a directory path fails with IOException on
            // both Windows and Linux) — ensuring we reach the catch(saveEx) branch and a
            // recovery snapshot is written to the separate .recovery.json path.
            bool patchRan = false;
            string tmpPath = registryPath + ".tmp";

            Hina.Core.Patching.IPatchClient CorruptingPatcher(Hina.Core.Configuration.PatcherConfig cfg) =>
                new CorruptThenSucceedPatchClient(cfg, () =>
                {
                    patchRan = true;
                    // Corrupt registry.json so Load() throws RegistryCorruptException.
                    File.WriteAllText(registryPath, "{ NOT VALID JSON !!!");
                    // Create a directory at the .tmp path: opening it as a file for write fails
                    // cross-platform, so SaveAsync cannot write and throws IOException.
                    if (File.Exists(tmpPath)) File.Delete(tmpPath);
                    Directory.CreateDirectory(tmpPath);
                });

            UpdateService svc = new UpdateService(
                _paths, _platform,
                fetcher: new StubFetcher(v2),
                patchClientFactory: CorruptingPatcher);

            UpdateResult result = await svc.UpdateAsync("demo", null, CancellationToken.None);

            Assert.True(patchRan, "Patch did not run — test setup is wrong.");
            Assert.Equal(UpdateStatus.Failed, result.Status);

            // Clean up the blocking directory so temp-dir cleanup can remove it.
            if (Directory.Exists(tmpPath)) Directory.Delete(tmpPath);

            // The recovery snapshot must exist and must contain the v2 row, not the stale v1.
            Assert.True(File.Exists(recoveryPath), "Recovery snapshot was not written.");
            Registry.Registry recovered = new RegistryStore(recoveryPath).Load();
            Assert.True(recovered.Apps.ContainsKey("demo"), "Recovery snapshot missing 'demo' row.");
            Assert.Equal("1.1.0", recovered.Apps["demo"].InstalledVersion);
        }

        [Fact]
        public async Task Update_CancelledDuringFetch_PropagatesCancellation()
        {
            // Ctrl+C during the descriptor re-fetch must propagate as cancellation, not be
            // reported as UpdateStatus.Failed — `update --all` relies on the exception to abort.
            await InstallV1();

            using var cts = new CancellationTokenSource();
            UpdateService svc = new UpdateService(
                _paths, _platform,
                fetcher: new CancellingFetcher(cts),
                patchClientFactory: cfg => new FakePatchClient(cfg, NewExecFiles()));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => svc.UpdateAsync("demo", null, cts.Token));

            Registry.Registry reg = new RegistryStore(_paths.RegistryFile).Load();
            Assert.Equal("1.0.0", reg.Apps["demo"].InstalledVersion);
        }

        [Fact]
        public async Task Update_CancelledDuringPatch_RollsBackAndPropagates()
        {
            await InstallV1();

            AppDescriptor v2 = BuildDescriptor(_pubKey, version: "1.1.0");
            DescriptorSigner.AttachSignature(v2, Convert.FromBase64String(_privKey));

            using var cts = new CancellationTokenSource();
            UpdateService svc = new UpdateService(
                _paths, _platform,
                fetcher: new StubFetcher(v2),
                patchClientFactory: cfg => new CancellingPatchClient(cfg, cts));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => svc.UpdateAsync("demo", null, cts.Token));

            // Registry row restored to the pre-update snapshot.
            Registry.Registry reg = new RegistryStore(_paths.RegistryFile).Load();
            Assert.Equal("1.0.0", reg.Apps["demo"].InstalledVersion);
        }

        [Fact]
        public async Task Update_CancelledDuringHookAdd_RollsBackAndPropagates()
        {
            await InstallV1();

            // v2 renames the shell entry so the diff has both a removal and an addition;
            // the cancellation fires on the add-phase CreateMenuShortcut.
            AppDescriptor v2 = BuildDescriptor(_pubKey, version: "1.1.0");
            v2.Entries[0].Id = "main2";
            DescriptorSigner.AttachSignature(v2, Convert.FromBase64String(_privKey));

            using var cts = new CancellationTokenSource();
            _platform.CancelOnCreateShortcut = cts;
            UpdateService svc = new UpdateService(
                _paths, _platform,
                fetcher: new StubFetcher(v2),
                patchClientFactory: cfg => new FakePatchClient(cfg, NewExecFiles()));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => svc.UpdateAsync("demo", null, cts.Token));

            Registry.Registry reg = new RegistryStore(_paths.RegistryFile).Load();
            Assert.Equal("1.0.0", reg.Apps["demo"].InstalledVersion);
        }

        [Fact]
        public async Task Update_DescriptorRenamesApp_IsRefused()
        {
            // The pinned key and the version direction are both guarded, but the app's
            // primary identity wasn't: a re-fetched descriptor declaring a different name
            // updated fine and refreshed the descriptor cache with the new name, leaving
            // the registry row keyed "demo" with a cached descriptor claiming "demo2".
            await InstallV1();

            AppDescriptor renamed = BuildDescriptor(_pubKey, name: "demo2", version: "1.1.0");
            DescriptorSigner.AttachSignature(renamed, Convert.FromBase64String(_privKey));

            UpdateService svc = new UpdateService(
                _paths, _platform,
                fetcher: new StubFetcher(renamed),
                patchClientFactory: cfg => new FakePatchClient(cfg, NewExecFiles()));

            UpdateResult result = await svc.UpdateAsync("demo", null, CancellationToken.None);

            Assert.Equal(UpdateStatus.Failed, result.Status);
            Assert.Contains("demo2", result.Message);
            Registry.Registry reg = new RegistryStore(_paths.RegistryFile).Load();
            Assert.Equal("1.0.0", reg.Apps["demo"].InstalledVersion);
        }

        [Fact]
        public async Task Update_CancelledAfterHookAdd_StillCommitsRegistry()
        {
            // Ctrl+C landing between the end of the add-phase and the final registry write:
            // every piece of work is already done (files v2, hooks/entries v2), so the commit
            // must complete instead of leaving files at v2 with the registry still at v1 and
            // reporting a bogus "registry could not be saved: operation was canceled" failure.
            await InstallV1();

            AppDescriptor v2 = BuildDescriptor(_pubKey, version: "1.1.0");
            v2.Entries[0].Id = "main2";
            DescriptorSigner.AttachSignature(v2, Convert.FromBase64String(_privKey));

            using var cts = new CancellationTokenSource();
            _platform.CancelAfterCreateShortcut = cts;
            UpdateService svc = new UpdateService(
                _paths, _platform,
                fetcher: new StubFetcher(v2),
                patchClientFactory: cfg => new FakePatchClient(cfg, NewExecFiles()));

            UpdateResult result = await svc.UpdateAsync("demo", null, cts.Token);

            Assert.Equal(UpdateStatus.Updated, result.Status);
            Registry.Registry reg = new RegistryStore(_paths.RegistryFile).Load();
            Assert.Equal("1.1.0", reg.Apps["demo"].InstalledVersion);
        }

        private static AppDescriptor BuildDescriptor(string publicKey, string name = "demo", string version = "1.0.0")
        {
            return new AppDescriptor
            {
                SchemaVersion = 1,
                Name = name,
                DisplayName = name,
                Version = version,
                Publisher = "Acme",
                BaseUrl = $"https://cdn.example.com/{name}/",
                PublicKey = publicKey,
                Exec = new ExecMap { Windows = "bin\\demo.exe", Linux = "bin/demo", Macos = "bin/demo" },
                Entries = { new ShellEntry { Id = "main", Name = name, Exec = ExecRelative() } },
                PostInstall = { new AddToPathHook { Name = "demo", Target = ExecRelative() } }
            };
        }

        private static string ExecRelative() =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "bin\\demo.exe" : "bin/demo";

        private static Dictionary<string, byte[]> NewExecFiles() => new()
        {
            [ExecRelative()] = new byte[] { 1, 2, 3 }
        };
    }

    // Routes each descriptor URL to its own stub descriptor (multi-app update tests).
    internal sealed class RoutingFetcher : DescriptorFetcher
    {
        private readonly Func<Uri, AppDescriptor> _route;
        public RoutingFetcher(Func<Uri, AppDescriptor> route) { _route = route; }
        public override Task<AppDescriptor> FetchAsync(Uri url, CancellationToken ct) => Task.FromResult(_route(url));
    }

    // Runs a side-effect (simulating a concurrent registry writer) then reports patch failure.
    internal sealed class ConcurrentWriterThenFailPatchClient : Hina.Core.Patching.IPatchClient
    {
        private readonly System.Action _onPatch;
        public ConcurrentWriterThenFailPatchClient(Hina.Core.Configuration.PatcherConfig config, System.Action onPatch)
        {
            Config = config;
            _onPatch = onPatch;
        }
        public Hina.Core.Configuration.PatcherConfig Config { get; }
        public Task<Hina.Core.Patching.CheckResult> CheckAsync(string rootDir, CancellationToken ct)
            => Task.FromResult(new Hina.Core.Patching.CheckResult { IsUpdateAvailable = true });
        public Task<Hina.Core.Patching.PatchResult> PatchAsync(string rootDir, CancellationToken ct)
        {
            _onPatch();
            return Task.FromResult(new Hina.Core.Patching.PatchResult { Success = false, Message = "simulated failure" });
        }
        public Task<Hina.Core.Patching.VerifyResult> VerifyAsync(string rootDir, CancellationToken ct)
            => Task.FromResult(new Hina.Core.Patching.VerifyResult { Success = true });
        public Task RollbackAsync(string rootDir, CancellationToken ct) => Task.CompletedTask;
        public void Dispose() { }
    }

    // Simulates Ctrl+C arriving while the descriptor fetch is in flight.
    internal sealed class CancellingFetcher : DescriptorFetcher
    {
        private readonly CancellationTokenSource _cts;
        public CancellingFetcher(CancellationTokenSource cts) { _cts = cts; }
        public override Task<AppDescriptor> FetchAsync(Uri url, CancellationToken ct)
        {
            _cts.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unreachable");
        }
    }

    // Simulates Ctrl+C arriving while the patch download is in flight.
    internal sealed class CancellingPatchClient : Hina.Core.Patching.IPatchClient
    {
        private readonly CancellationTokenSource _cts;
        public CancellingPatchClient(Hina.Core.Configuration.PatcherConfig config, CancellationTokenSource cts)
        {
            Config = config;
            _cts = cts;
        }
        public Hina.Core.Configuration.PatcherConfig Config { get; }
        public Task<Hina.Core.Patching.CheckResult> CheckAsync(string rootDir, CancellationToken ct)
            => Task.FromResult(new Hina.Core.Patching.CheckResult { IsUpdateAvailable = true });
        public Task<Hina.Core.Patching.PatchResult> PatchAsync(string rootDir, CancellationToken ct)
        {
            _cts.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unreachable");
        }
        public Task<Hina.Core.Patching.VerifyResult> VerifyAsync(string rootDir, CancellationToken ct)
            => Task.FromResult(new Hina.Core.Patching.VerifyResult { Success = true });
        public Task RollbackAsync(string rootDir, CancellationToken ct) => Task.CompletedTask;
        public void Dispose() { }
    }

    // Multi-URL stub fetcher so UpdateAllAsync can map descriptorUrl → descriptor.
    internal sealed class MultiStubFetcher : DescriptorFetcher
    {
        private readonly Dictionary<string, AppDescriptor> _byUrl;
        public MultiStubFetcher(Dictionary<string, AppDescriptor> byUrl) : base() => _byUrl = byUrl;
        public override Task<AppDescriptor> FetchAsync(Uri url, CancellationToken ct)
            => Task.FromResult(_byUrl[url.ToString()]);
    }

    // Runs a side-effect (e.g. corrupting the registry file) then reports patch SUCCESS.
    // Used by BUG-014 test to corrupt the registry after the patch so that the subsequent
    // store.Load() inside the final commit try throws, exercising the recovery snapshot path.
    internal sealed class CorruptThenSucceedPatchClient : Hina.Core.Patching.IPatchClient
    {
        private readonly System.Action _onPatch;
        public CorruptThenSucceedPatchClient(Hina.Core.Configuration.PatcherConfig config, System.Action onPatch)
        {
            Config = config;
            _onPatch = onPatch;
        }
        public Hina.Core.Configuration.PatcherConfig Config { get; }
        public Task<Hina.Core.Patching.CheckResult> CheckAsync(string rootDir, CancellationToken ct)
            => Task.FromResult(new Hina.Core.Patching.CheckResult { IsUpdateAvailable = true });
        public Task<Hina.Core.Patching.PatchResult> PatchAsync(string rootDir, CancellationToken ct)
        {
            _onPatch();
            return Task.FromResult(new Hina.Core.Patching.PatchResult { Success = true });
        }
        public Task<Hina.Core.Patching.VerifyResult> VerifyAsync(string rootDir, CancellationToken ct)
            => Task.FromResult(new Hina.Core.Patching.VerifyResult { Success = true });
        public Task RollbackAsync(string rootDir, CancellationToken ct) => Task.CompletedTask;
        public void Dispose() { }
    }
}
