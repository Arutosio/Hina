using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Crypto;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Install;
using Hina.PackageManager.Net;
using Hina.PackageManager.Paths;
using Hina.PackageManager.Platform;
using Hina.PackageManager.Registry;

namespace Hina.PackageManager.Tests
{
    // Round-2 audit fixes:
    // B1 update hook-add failure rolls back, B2 cancellation propagates,
    // B3 SharedHttp singleton, B4 DescriptorFetcher retries on 5xx,
    // U1 symlink-safe uninstall, O2 update-all parallelism.
    public class Round2AuditTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly InstallPaths _paths;
        private readonly FakePlatformIntegration _platform;
        private readonly string _privKey;
        private readonly string _pubKey;

        public Round2AuditTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "hina-r2-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_tempDir);
            _paths = InstallPaths.ForRoot(_tempDir);
            _platform = new FakePlatformIntegration();
            (_privKey, _pubKey) = KeyGenerator.GenerateEd25519();
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, recursive: true); } catch { }
            }
        }

        // ---- B3: SharedHttp singleton ----

        [Fact]
        public void SharedHttp_Instance_IsSingleton()
        {
            HttpClient a = SharedHttp.Instance;
            HttpClient b = SharedHttp.Instance;
            Assert.Same(a, b);
        }

        [Fact]
        public void SharedHttp_HasHinaUserAgent()
        {
            Assert.Contains(SharedHttp.Instance.DefaultRequestHeaders.UserAgent,
                p => p.Product != null && p.Product.Name == "Hina");
        }

        // ---- B4: DescriptorFetcher retry on 5xx ----

        [Fact]
        public async Task DescriptorFetcher_RetriesOn503_ThenSucceeds()
        {
            FlakyHandler handler = new FlakyHandler(failBefore: 2);
            HttpClient client = new HttpClient(handler);

            DescriptorFetcher fetcher = new DescriptorFetcher(client, maxRetries: 3, retryBaseDelay: TimeSpan.FromMilliseconds(5));

            AppDescriptor d = await fetcher.FetchAsync(new Uri("https://example.com/x.json"), CancellationToken.None);

            Assert.Equal("demo", d.Name);
            Assert.Equal(3, handler.Calls);
        }

        [Fact]
        public async Task DescriptorFetcher_DoesNotRetryOn404()
        {
            FlakyHandler handler = new FlakyHandler(alwaysReturn: HttpStatusCode.NotFound);
            HttpClient client = new HttpClient(handler);

            DescriptorFetcher fetcher = new DescriptorFetcher(client, maxRetries: 3, retryBaseDelay: TimeSpan.FromMilliseconds(5));

            await Assert.ThrowsAsync<HttpRequestException>(() =>
                fetcher.FetchAsync(new Uri("https://example.com/x.json"), CancellationToken.None));

            Assert.Equal(1, handler.Calls);  // single call, no retry
        }

        [Fact]
        public async Task DescriptorFetcher_RejectsNonHttpScheme()
        {
            DescriptorFetcher fetcher = new DescriptorFetcher();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fetcher.FetchAsync(new Uri("file:///etc/passwd"), CancellationToken.None));
        }

        // ---- B1: UpdateService rolls back when hook-add fails mid-flight ----

        [Fact]
        public async Task Update_HookAddFails_RollsBackToPreviousVersion()
        {
            await InstallV1();

            FailingHookPlatform failPlatform = new FailingHookPlatform();
            // Re-install under failPlatform so it owns the registry state.
            (string priv2, string pub2) = KeyGenerator.GenerateEd25519();
            AppDescriptor v1 = SignedDescriptor("demo2", "1.0.0", pub2, priv2);
            InstallService installSvc = new InstallService(_paths, failPlatform,
                fetcher: new StubFetcher(v1),
                patchClientFactory: cfg => new FakePatchClient(cfg, NewExecFiles()));
            await installSvc.InstallAsync(new Uri("https://example.com/demo2.json"), null, CancellationToken.None);

            // v2 adds a registerAutostart hook; failPlatform throws on RegisterAutostart.
            // Build the descriptor THEN sign (signing must happen last — any mutation
            // after AttachSignature invalidates the signature).
            AppDescriptor v2 = new AppDescriptor
            {
                SchemaVersion = 1,
                Name = "demo2",
                DisplayName = "demo2",
                Version = "1.1.0",
                Publisher = "Acme",
                BaseUrl = "https://cdn.example.com/demo2/",
                PublicKey = pub2,
                Exec = new ExecMap { Windows = "bin\\demo.exe", Linux = "bin/demo", Macos = "bin/demo" },
                Entries = { new ShellEntry { Id = "main", Name = "demo2", Exec = ExecRelative() } },
                PostInstall =
                {
                    new AddToPathHook { Name = "demo2", Target = ExecRelative() },
                    new AutostartHook { EntryId = "main" }
                }
            };
            DescriptorSigner.AttachSignature(v2, Convert.FromBase64String(priv2));

            UpdateService svc = new UpdateService(_paths, failPlatform,
                fetcher: new StubFetcher(v2),
                patchClientFactory: cfg => new FakePatchClient(cfg, NewExecFiles()));

            UpdateResult r = await svc.UpdateAsync("demo2", null, CancellationToken.None);

            Assert.Equal(UpdateStatus.Failed, r.Status);
            Assert.Contains("rolled back", r.Message);

            // Registry restored to v1.
            Registry.Registry reg = new RegistryStore(_paths.RegistryFile).Load();
            Assert.Equal("1.0.0", reg.Apps["demo2"].InstalledVersion);
        }

        // ---- U1: symlink-safe uninstall ----

        [Fact]
        public async Task Uninstall_InstallPathIsSymlink_OnlyDeletesLinkNotTarget()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Symlink creation needs Developer Mode or admin on Windows; skip.
                return;
            }

            // Real install puts data at /tempDir/Apps/demo. Replace that with a
            // symlink to /tempDir/somewhere-else holding the actual payload.
            await InstallV1();
            string realPayload = Path.Combine(_tempDir, "real-payload");
            string installLink = _paths.AppDir("demo");
            // Move real dir contents aside, replace install dir with symlink to them.
            Directory.Move(installLink, realPayload);
            File.CreateSymbolicLink(installLink, realPayload);
            string canaryFile = Path.Combine(realPayload, "do-not-delete.txt");
            File.WriteAllText(canaryFile, "keep me");

            UninstallService u = new UninstallService(_paths, _platform);
            await u.UninstallAsync("demo", CancellationToken.None);

            // Link gone, target intact.
            Assert.False(Directory.Exists(installLink) || File.Exists(installLink));
            Assert.True(File.Exists(canaryFile));
        }

        // ---- O2: UpdateAllAsync parallelism ----

        [Fact]
        public async Task UpdateAllAsync_RunsConcurrently()
        {
            // Install 4 apps then bump them all. Each FakePatchClient sleeps briefly so
            // serial would take 4 * delay; parallel finishes in ~1 delay.
            (string priv, string pub) = (_privKey, _pubKey);
            for (int i = 0; i < 4; i++)
            {
                AppDescriptor d = SignedDescriptor("app" + i, "1.0.0", pub, priv);
                InstallService s = new InstallService(_paths, _platform,
                    fetcher: new StubFetcher(d),
                    patchClientFactory: cfg => new FakePatchClient(cfg, NewExecFiles()));
                await s.InstallAsync(new Uri("https://example.com/app" + i + ".json"), null, CancellationToken.None);
            }

            Dictionary<string, AppDescriptor> v2Map = new();
            for (int i = 0; i < 4; i++)
            {
                v2Map["https://example.com/app" + i + ".json"] = SignedDescriptor("app" + i, "1.1.0", pub, priv);
            }

            UpdateService svc = new UpdateService(_paths, _platform,
                fetcher: new MultiStubFetcher(v2Map),
                patchClientFactory: cfg => new SlowFakePatchClient(cfg, NewExecFiles(), TimeSpan.FromMilliseconds(150)));

            DateTimeOffset start = DateTimeOffset.UtcNow;
            List<UpdateResult> results = await svc.UpdateAllAsync(new UpdateOptions { MaxParallelism = 4 }, CancellationToken.None);
            TimeSpan elapsed = DateTimeOffset.UtcNow - start;

            foreach (UpdateResult r in results)
            {
                Assert.Equal(UpdateStatus.Updated, r.Status);
            }

            // 4 apps * 150ms each = 600ms serial. With 4 jobs we expect well under 500ms.
            // Pad generously to avoid flakes on slow CI runners.
            Assert.True(elapsed < TimeSpan.FromMilliseconds(500),
                $"UpdateAllAsync was not parallel: took {elapsed.TotalMilliseconds:F0}ms for 4 apps");
        }

        // ---- B2: cancellation ----

        [Fact]
        public async Task DescriptorFetcher_HonoursCancellation()
        {
            SlowHandler slow = new SlowHandler(TimeSpan.FromSeconds(10));
            HttpClient client = new HttpClient(slow);
            DescriptorFetcher fetcher = new DescriptorFetcher(client, maxRetries: 1, retryBaseDelay: TimeSpan.FromMilliseconds(5));

            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                fetcher.FetchAsync(new Uri("https://example.com/x.json"), cts.Token));
        }

        // ---- Helpers ----

        private async Task InstallV1(string name = "demo")
        {
            AppDescriptor v1 = SignedDescriptor(name, "1.0.0", _pubKey, _privKey);
            InstallService svc = new InstallService(_paths, _platform,
                fetcher: new StubFetcher(v1),
                patchClientFactory: cfg => new FakePatchClient(cfg, NewExecFiles()));
            await svc.InstallAsync(new Uri($"https://example.com/{name}.json"), null, CancellationToken.None);
        }

        private static AppDescriptor SignedDescriptor(string name, string version, string pub, string priv)
        {
            AppDescriptor d = new AppDescriptor
            {
                SchemaVersion = 1,
                Name = name,
                DisplayName = name,
                Version = version,
                Publisher = "Acme",
                BaseUrl = $"https://cdn.example.com/{name}/",
                PublicKey = pub,
                Exec = new ExecMap { Windows = "bin\\demo.exe", Linux = "bin/demo", Macos = "bin/demo" },
                Entries = { new ShellEntry { Id = "main", Name = name, Exec = ExecRelative() } },
                PostInstall = { new AddToPathHook { Name = name, Target = ExecRelative() } }
            };
            DescriptorSigner.AttachSignature(d, Convert.FromBase64String(priv));
            return d;
        }

        private static string ExecRelative() =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "bin\\demo.exe" : "bin/demo";

        private static Dictionary<string, byte[]> NewExecFiles() => new()
        {
            [ExecRelative()] = new byte[] { 1, 2, 3 }
        };
    }

    // Fakes / handlers below.

    internal sealed class FlakyHandler : HttpMessageHandler
    {
        public int Calls;
        private readonly int _failBefore;
        private readonly HttpStatusCode? _always;

        public FlakyHandler(int failBefore = 0, HttpStatusCode? alwaysReturn = null)
        {
            _failBefore = failBefore;
            _always = alwaysReturn;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref Calls);
            if (_always.HasValue)
            {
                return Task.FromResult(new HttpResponseMessage(_always.Value));
            }
            if (call <= _failBefore)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }
            // Build a tiny signed descriptor so DescriptorParser/Validator accept it.
            (string priv, string pub) = KeyGenerator.GenerateEd25519();
            AppDescriptor d = new AppDescriptor
            {
                SchemaVersion = 1,
                Name = "demo",
                DisplayName = "Demo",
                Version = "1.0.0",
                Publisher = "Acme",
                BaseUrl = "https://cdn.example.com/demo/",
                PublicKey = pub,
                Exec = new ExecMap { Linux = "bin/demo", Macos = "bin/demo", Windows = "bin\\demo.exe" },
                Entries = { new ShellEntry { Id = "main", Name = "Demo", Exec = "bin/demo" } }
            };
            DescriptorSigner.AttachSignature(d, Convert.FromBase64String(priv));
            string json = DescriptorParser.Serialize(d);
            HttpResponseMessage ok = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(ok);
        }
    }

    internal sealed class SlowHandler : HttpMessageHandler
    {
        private readonly TimeSpan _delay;
        public SlowHandler(TimeSpan delay) { _delay = delay; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(_delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
        }
    }

    // FakePlatformIntegration that throws on RegisterAutostart to exercise B1.
    internal sealed class FailingHookPlatform : IPlatformIntegration
    {
        private readonly FakePlatformIntegration _delegateImpl = new FakePlatformIntegration();

        public string OsId => _delegateImpl.OsId;
        public string UserBinDir => _delegateImpl.UserBinDir;
        public string UserAppsDir => _delegateImpl.UserAppsDir;

        public Task<string> CreateMenuShortcut(ShellEntry entry, string appDir, CancellationToken ct) => _delegateImpl.CreateMenuShortcut(entry, appDir, ct);
        public Task RemoveMenuShortcut(string evidencePath, CancellationToken ct) => _delegateImpl.RemoveMenuShortcut(evidencePath, ct);
        public Task<string> AddToPath(string name, string targetExec, CancellationToken ct) => _delegateImpl.AddToPath(name, targetExec, ct);
        public Task RemoveFromPath(string evidencePath, CancellationToken ct) => _delegateImpl.RemoveFromPath(evidencePath, ct);
        public Task<string> RegisterMimeType(MimeTypeHook hook, string appDir, CancellationToken ct) => _delegateImpl.RegisterMimeType(hook, appDir, ct);
        public Task UnregisterMimeType(string evidencePath, CancellationToken ct) => _delegateImpl.UnregisterMimeType(evidencePath, ct);
        public Task<string> RegisterUrlScheme(UrlSchemeHook hook, string appDir, CancellationToken ct) => _delegateImpl.RegisterUrlScheme(hook, appDir, ct);
        public Task UnregisterUrlScheme(string evidencePath, CancellationToken ct) => _delegateImpl.UnregisterUrlScheme(evidencePath, ct);
        public Task<string> InstallFont(string fontFile, CancellationToken ct) => _delegateImpl.InstallFont(fontFile, ct);
        public Task UninstallFont(string evidencePath, CancellationToken ct) => _delegateImpl.UninstallFont(evidencePath, ct);

        public Task<string> RegisterAutostart(AutostartHook hook, string appDir, CancellationToken ct)
            => throw new UnauthorizedAccessException("autostart denied (test)");

        public Task UnregisterAutostart(string evidencePath, CancellationToken ct) => _delegateImpl.UnregisterAutostart(evidencePath, ct);
    }

    // FakePatchClient that sleeps before reporting success — used to measure
    // serial-vs-parallel timing in O2.
    internal sealed class SlowFakePatchClient : Hina.Core.Patching.IPatchClient
    {
        private readonly Dictionary<string, byte[]> _files;
        private readonly TimeSpan _delay;

        public SlowFakePatchClient(Hina.Core.Configuration.PatcherConfig config, Dictionary<string, byte[]> files, TimeSpan delay)
        {
            Config = config;
            _files = files;
            _delay = delay;
        }

        public Hina.Core.Configuration.PatcherConfig Config { get; }

        public Task<Hina.Core.Patching.CheckResult> CheckAsync(string rootDir, CancellationToken ct) =>
            Task.FromResult(new Hina.Core.Patching.CheckResult { IsUpdateAvailable = true });

        public async Task<Hina.Core.Patching.PatchResult> PatchAsync(string rootDir, CancellationToken ct)
        {
            await Task.Delay(_delay, ct);
            foreach ((string rel, byte[] bytes) in _files)
            {
                string abs = Path.Combine(rootDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
                File.WriteAllBytes(abs, bytes);
            }
            return new Hina.Core.Patching.PatchResult { Success = true };
        }

        public Task<Hina.Core.Patching.VerifyResult> VerifyAsync(string rootDir, CancellationToken ct) =>
            Task.FromResult(new Hina.Core.Patching.VerifyResult { Success = true });

        public Task RollbackAsync(string rootDir, CancellationToken ct) => Task.CompletedTask;
    }
}
