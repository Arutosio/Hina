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
        public async Task Update_UnknownApp_ReturnsFailedWithMessage()
        {
            UpdateService svc = new UpdateService(_paths, _platform);

            UpdateResult result = await svc.UpdateAsync("ghost", null, CancellationToken.None);

            Assert.Equal(UpdateStatus.Failed, result.Status);
            Assert.Contains("not installed", result.Message);
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
            await install.InstallAsync(new Uri(descriptorUrl), null, CancellationToken.None);
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

    // Multi-URL stub fetcher so UpdateAllAsync can map descriptorUrl → descriptor.
    internal sealed class MultiStubFetcher : DescriptorFetcher
    {
        private readonly Dictionary<string, AppDescriptor> _byUrl;
        public MultiStubFetcher(Dictionary<string, AppDescriptor> byUrl) : base() => _byUrl = byUrl;
        public override Task<AppDescriptor> FetchAsync(Uri url, CancellationToken ct)
            => Task.FromResult(_byUrl[url.ToString()]);
    }
}
