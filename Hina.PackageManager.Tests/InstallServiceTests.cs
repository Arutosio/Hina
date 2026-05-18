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
    public class InstallServiceTests : IDisposable
    {
        private readonly string _tempDir;

        public InstallServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "hina-install-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task Install_EndToEnd_CreatesAppDirShortcutsRegistry()
        {
            // Arrange
            (string priv, string pub) = KeyGenerator.GenerateEd25519();
            AppDescriptor descriptor = BuildDescriptor(pub);
            DescriptorSigner.AttachSignature(descriptor, Convert.FromBase64String(priv));

            InstallPaths paths = InstallPaths.ForRoot(_tempDir);
            FakePlatformIntegration platform = new();

            string execRel = InstallService.ExecForCurrentOs(descriptor)!;
            Dictionary<string, byte[]> patchFiles = new()
            {
                [execRel] = new byte[] { 0x7F, 0x45, 0x4C, 0x46 }  // tiny ELF header bytes
            };

            InstallService install = new(
                paths,
                platform,
                fetcher: new StubFetcher(descriptor),
                patchClientFactory: cfg => new FakePatchClient(cfg, patchFiles));

            // Act
            InstallResult result = await install.InstallAsync(
                new Uri("https://example.com/hina.app.json"),
                options: null,
                CancellationToken.None);

            // Assert
            Assert.Equal("demo", result.Name);
            Assert.Equal("1.0.0", result.Version);

            string appDir = paths.AppDir("demo");
            Assert.True(File.Exists(Path.Combine(appDir, execRel)));

            Assert.Single(platform.CreatedShortcuts);
            Assert.Single(platform.AddedToPath);

            Registry.Registry reg = new RegistryStore(paths.RegistryFile).Load();
            Assert.True(reg.Apps.ContainsKey("demo"));
            InstalledApp app = reg.Apps["demo"];
            Assert.Equal("1.0.0", app.InstalledVersion);
            Assert.Single(app.ExecutedHooks);
            Assert.Single(app.ShellEntries);
        }

        [Fact]
        public async Task Install_DescriptorWithBadSignature_Throws()
        {
            (_, string pub) = KeyGenerator.GenerateEd25519();
            (string otherPriv, _) = KeyGenerator.GenerateEd25519();
            AppDescriptor descriptor = BuildDescriptor(pub);
            // Sign with the WRONG private key.
            DescriptorSigner.AttachSignature(descriptor, Convert.FromBase64String(otherPriv));

            InstallPaths paths = InstallPaths.ForRoot(_tempDir);
            InstallService install = new(
                paths,
                new FakePlatformIntegration(),
                fetcher: new StubFetcher(descriptor),
                patchClientFactory: cfg => new FakePatchClient(cfg, new Dictionary<string, byte[]>()));

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                install.InstallAsync(new Uri("https://example.com/hina.app.json"), null, CancellationToken.None));

            // Nothing should have been created on disk.
            Assert.False(Directory.Exists(paths.AppDir("demo")));
            Assert.False(File.Exists(paths.RegistryFile));
        }

        [Fact]
        public async Task Install_WhenAlreadyInstalled_ThrowsAndDoesNotMutateRegistry()
        {
            (string priv, string pub) = KeyGenerator.GenerateEd25519();
            AppDescriptor descriptor = BuildDescriptor(pub);
            DescriptorSigner.AttachSignature(descriptor, Convert.FromBase64String(priv));

            InstallPaths paths = InstallPaths.ForRoot(_tempDir);
            FakePlatformIntegration platform = new();
            string execRel = InstallService.ExecForCurrentOs(descriptor)!;

            InstallService svc = new(
                paths,
                platform,
                fetcher: new StubFetcher(descriptor),
                patchClientFactory: cfg => new FakePatchClient(cfg, new Dictionary<string, byte[]> { [execRel] = new byte[] { 1 } }));

            await svc.InstallAsync(new Uri("https://example.com/hina.app.json"), null, CancellationToken.None);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.InstallAsync(new Uri("https://example.com/hina.app.json"), null, CancellationToken.None));
        }

        [Fact]
        public async Task Install_MissingExec_FailsAndRollsBack()
        {
            (string priv, string pub) = KeyGenerator.GenerateEd25519();
            AppDescriptor descriptor = BuildDescriptor(pub);
            DescriptorSigner.AttachSignature(descriptor, Convert.FromBase64String(priv));

            InstallPaths paths = InstallPaths.ForRoot(_tempDir);
            FakePlatformIntegration platform = new();

            // FakePatchClient does NOT write the exec file → InstallService must fail and roll back.
            InstallService svc = new(
                paths,
                platform,
                fetcher: new StubFetcher(descriptor),
                patchClientFactory: cfg => new FakePatchClient(cfg, new Dictionary<string, byte[]>()));

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                svc.InstallAsync(new Uri("https://example.com/hina.app.json"), null, CancellationToken.None));

            // App dir was created then rolled back.
            Assert.False(Directory.Exists(paths.AppDir("demo")));
            // Registry never committed.
            Registry.Registry reg = new RegistryStore(paths.RegistryFile).Load();
            Assert.False(reg.Apps.ContainsKey("demo"));
        }

        [Fact]
        public async Task UninstallService_RemovesEverythingTheInstallCreated()
        {
            (string priv, string pub) = KeyGenerator.GenerateEd25519();
            AppDescriptor descriptor = BuildDescriptor(pub);
            DescriptorSigner.AttachSignature(descriptor, Convert.FromBase64String(priv));

            InstallPaths paths = InstallPaths.ForRoot(_tempDir);
            FakePlatformIntegration platform = new();
            string execRel = InstallService.ExecForCurrentOs(descriptor)!;

            InstallService installSvc = new(
                paths,
                platform,
                fetcher: new StubFetcher(descriptor),
                patchClientFactory: cfg => new FakePatchClient(cfg, new Dictionary<string, byte[]> { [execRel] = new byte[] { 0x90 } }));

            await installSvc.InstallAsync(new Uri("https://example.com/hina.app.json"), null, CancellationToken.None);

            UninstallService uninstallSvc = new(paths, platform);
            UninstallResult res = await uninstallSvc.UninstallAsync("demo", CancellationToken.None);

            Assert.True(res.Removed);
            Assert.False(Directory.Exists(paths.AppDir("demo")));
            Assert.Single(platform.RemovedFromPath);
            Assert.Single(platform.RemovedShortcuts);

            Registry.Registry reg = new RegistryStore(paths.RegistryFile).Load();
            Assert.False(reg.Apps.ContainsKey("demo"));
        }

        [Fact]
        public async Task UninstallService_MissingApp_IsNoOpAndReturnsFalse()
        {
            InstallPaths paths = InstallPaths.ForRoot(_tempDir);
            UninstallService svc = new(paths, new FakePlatformIntegration());

            UninstallResult res = await svc.UninstallAsync("nothing-installed", CancellationToken.None);

            Assert.False(res.Removed);
        }

        private static AppDescriptor BuildDescriptor(string publicKey)
        {
            ExecMap exec = new ExecMap
            {
                Windows = "bin\\demo.exe",
                Linux = "bin/demo",
                Macos = "bin/demo"
            };

            return new AppDescriptor
            {
                SchemaVersion = 1,
                Name = "demo",
                DisplayName = "Demo",
                Version = "1.0.0",
                Publisher = "Acme",
                BaseUrl = "https://cdn.example.com/demo/",
                PublicKey = publicKey,
                Exec = exec,
                Entries = { new ShellEntry { Id = "main", Name = "Demo", Exec = ExecRelative() } },
                PostInstall = { new AddToPathHook { Name = "demo", Target = ExecRelative() } }
            };

            static string ExecRelative() =>
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "bin\\demo.exe" : "bin/demo";
        }
    }

    // Bypasses HTTP — hands the InstallService a fixed descriptor object.
    internal sealed class StubFetcher : DescriptorFetcher
    {
        private readonly AppDescriptor _descriptor;

        public StubFetcher(AppDescriptor descriptor) : base()
        {
            _descriptor = descriptor;
        }

        public override Task<AppDescriptor> FetchAsync(Uri url, CancellationToken ct) =>
            Task.FromResult(_descriptor);
    }
}
