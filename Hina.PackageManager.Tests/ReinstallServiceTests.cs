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
    public class ReinstallServiceTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly InstallPaths _paths;
        private readonly FakePlatformIntegration _platform;

        public ReinstallServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "hina-reinstall-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_tempDir);
            _paths = InstallPaths.ForRoot(_tempDir);
            _platform = new FakePlatformIntegration();
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task Reinstall_SameKey_Succeeds_AndAppRemainsRegistered()
        {
            (string priv, string pub) = KeyGenerator.GenerateEd25519();
            AppDescriptor d = BuildDescriptor(pub, "1.0.0");
            DescriptorSigner.AttachSignature(d, Convert.FromBase64String(priv));

            FakePatchClient PatchFactory(Hina.Core.Configuration.PatcherConfig cfg) =>
                new FakePatchClient(cfg, NewExecFiles());

            InstallService install = new InstallService(_paths, _platform, new StubFetcher(d), PatchFactory);
            await install.InstallAsync(new Uri("https://example.com/demo.json"), new InstallOptions { AssumeTrustOnFirstUse = true }, CancellationToken.None);

            ReinstallService svc = new ReinstallService(_paths, _platform, new StubFetcher(d), PatchFactory);
            InstallResult result = await svc.ReinstallAsync("demo", rotateKey: false, CancellationToken.None);

            Assert.Equal("demo", result.Name);
            Assert.Equal("1.0.0", result.Version);

            Registry.Registry reg = new RegistryStore(_paths.RegistryFile).Load();
            Assert.True(reg.Apps.ContainsKey("demo"));
            Assert.Equal(pub, reg.Apps["demo"].PublicKey);
        }

        [Fact]
        public async Task Reinstall_ForgedSignature_RefusesAndLeavesAppInstalled()
        {
            (string priv, string pub) = KeyGenerator.GenerateEd25519();
            AppDescriptor good = BuildDescriptor(pub, "1.0.0");
            DescriptorSigner.AttachSignature(good, Convert.FromBase64String(priv));

            FakePatchClient PatchFactory(Hina.Core.Configuration.PatcherConfig cfg) =>
                new FakePatchClient(cfg, NewExecFiles());

            InstallService install = new InstallService(_paths, _platform, new StubFetcher(good), PatchFactory);
            await install.InstallAsync(new Uri("https://example.com/demo.json"), new InstallOptions { AssumeTrustOnFirstUse = true }, CancellationToken.None);

            // Forge: keep the correct publicKey field (passes the equality check) but invalidate
            // the signature by mutating a signed field after signing.
            AppDescriptor forged = BuildDescriptor(pub, "1.0.0");
            DescriptorSigner.AttachSignature(forged, Convert.FromBase64String(priv));
            forged.Version = "1.0.1"; // signature no longer matches; publicKey unchanged

            ReinstallService svc = new ReinstallService(_paths, _platform, new StubFetcher(forged), PatchFactory);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.ReinstallAsync("demo", rotateKey: false, CancellationToken.None));

            Registry.Registry reg = new RegistryStore(_paths.RegistryFile).Load();
            Assert.True(reg.Apps.ContainsKey("demo"));
            Assert.Equal("1.0.0", reg.Apps["demo"].InstalledVersion);
        }

        [Fact]
        public async Task Reinstall_DifferentKey_WithoutRotateFlag_RefusesAndLeavesAppInstalled()
        {
            (string priv, string pub) = KeyGenerator.GenerateEd25519();
            AppDescriptor v1 = BuildDescriptor(pub, "1.0.0");
            DescriptorSigner.AttachSignature(v1, Convert.FromBase64String(priv));

            FakePatchClient PatchFactory(Hina.Core.Configuration.PatcherConfig cfg) =>
                new FakePatchClient(cfg, NewExecFiles());

            InstallService install = new InstallService(_paths, _platform, new StubFetcher(v1), PatchFactory);
            await install.InstallAsync(new Uri("https://example.com/demo.json"), new InstallOptions { AssumeTrustOnFirstUse = true }, CancellationToken.None);

            // Same name, same version, but rotated to a brand-new key pair.
            (string otherPriv, string otherPub) = KeyGenerator.GenerateEd25519();
            AppDescriptor rotated = BuildDescriptor(otherPub, "1.0.0");
            DescriptorSigner.AttachSignature(rotated, Convert.FromBase64String(otherPriv));

            ReinstallService svc = new ReinstallService(_paths, _platform, new StubFetcher(rotated), PatchFactory);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.ReinstallAsync("demo", rotateKey: false, CancellationToken.None));

            // App is still installed with the ORIGINAL pinned key — refusal happened
            // before uninstall.
            Registry.Registry reg = new RegistryStore(_paths.RegistryFile).Load();
            Assert.True(reg.Apps.ContainsKey("demo"));
            Assert.Equal(pub, reg.Apps["demo"].PublicKey);
        }

        [Fact]
        public async Task Reinstall_DifferentKey_WithRotateFlag_Succeeds_AndPinNewKey()
        {
            (string priv, string pub) = KeyGenerator.GenerateEd25519();
            AppDescriptor v1 = BuildDescriptor(pub, "1.0.0");
            DescriptorSigner.AttachSignature(v1, Convert.FromBase64String(priv));

            FakePatchClient PatchFactory(Hina.Core.Configuration.PatcherConfig cfg) =>
                new FakePatchClient(cfg, NewExecFiles());

            InstallService install = new InstallService(_paths, _platform, new StubFetcher(v1), PatchFactory);
            await install.InstallAsync(new Uri("https://example.com/demo.json"), new InstallOptions { AssumeTrustOnFirstUse = true }, CancellationToken.None);

            (string otherPriv, string otherPub) = KeyGenerator.GenerateEd25519();
            AppDescriptor rotated = BuildDescriptor(otherPub, "1.0.0");
            DescriptorSigner.AttachSignature(rotated, Convert.FromBase64String(otherPriv));

            ReinstallService svc = new ReinstallService(_paths, _platform, new StubFetcher(rotated), PatchFactory);
            InstallResult result = await svc.ReinstallAsync("demo", rotateKey: true, CancellationToken.None);

            Assert.Equal("demo", result.Name);

            Registry.Registry reg = new RegistryStore(_paths.RegistryFile).Load();
            Assert.Equal(otherPub, reg.Apps["demo"].PublicKey);
        }

        [Fact]
        public async Task Reinstall_ServerSwapsDescriptorAfterVerify_InstallsVerifiedOne()
        {
            // The descriptor verified before uninstall must be the exact one installed.
            // A server that returns a different (but self-signed) descriptor on the second
            // fetch must NOT get its swapped content installed.
            (string priv, string pub) = KeyGenerator.GenerateEd25519();
            AppDescriptor good = BuildDescriptor(pub, "1.0.0");
            DescriptorSigner.AttachSignature(good, Convert.FromBase64String(priv));

            FakePatchClient PatchFactory(Hina.Core.Configuration.PatcherConfig cfg) =>
                new FakePatchClient(cfg, NewExecFiles());

            InstallService install = new InstallService(_paths, _platform, new StubFetcher(good), PatchFactory);
            await install.InstallAsync(new Uri("https://example.com/demo.json"), new InstallOptions { AssumeTrustOnFirstUse = true }, CancellationToken.None);

            // Attacker descriptor: same name, brand-new key, new version, validly self-signed.
            (string evilPriv, string evilPub) = KeyGenerator.GenerateEd25519();
            AppDescriptor swapped = BuildDescriptor(evilPub, "9.9.9");
            DescriptorSigner.AttachSignature(swapped, Convert.FromBase64String(evilPriv));

            // First fetch (reinstall's pin/signature check) returns the good descriptor; any
            // later fetch returns the swapped one.
            SequencingFetcher fetcher = new SequencingFetcher(good, swapped);
            ReinstallService svc = new ReinstallService(_paths, _platform, fetcher, PatchFactory);
            await svc.ReinstallAsync("demo", rotateKey: false, CancellationToken.None);

            Registry.Registry reg = new RegistryStore(_paths.RegistryFile).Load();
            Assert.Equal(pub, reg.Apps["demo"].PublicKey);
            Assert.Equal("1.0.0", reg.Apps["demo"].InstalledVersion);
        }

        [Fact]
        public async Task Reinstall_UnknownApp_Throws()
        {
            FakePatchClient PatchFactory(Hina.Core.Configuration.PatcherConfig cfg) =>
                new FakePatchClient(cfg, new Dictionary<string, byte[]>());

            ReinstallService svc = new ReinstallService(_paths, _platform, patchClientFactory: PatchFactory);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.ReinstallAsync("ghost", rotateKey: false, CancellationToken.None));
        }

        private static AppDescriptor BuildDescriptor(string publicKey, string version)
        {
            ExecMap exec = new ExecMap { Windows = "bin\\demo.exe", Linux = "bin/demo", Macos = "bin/demo" };
            return new AppDescriptor
            {
                SchemaVersion = 1,
                Name = "demo",
                DisplayName = "Demo",
                Version = version,
                Publisher = "Acme",
                BaseUrl = "https://cdn.example.com/demo/",
                PublicKey = publicKey,
                Exec = exec,
                Entries = { new ShellEntry { Id = "main", Name = "Demo", Exec = ExecRelative() } },
                PostInstall = { new AddToPathHook { Name = "demo", Target = ExecRelative() } }
            };
        }

        private static string ExecRelative() =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "bin\\demo.exe" : "bin/demo";

        private static Dictionary<string, byte[]> NewExecFiles() => new()
        {
            [ExecRelative()] = new byte[] { 1, 2, 3 }
        };

        // Returns the first descriptor on the first fetch and the second on every later fetch,
        // simulating a server that swaps the descriptor between verify and install.
        private sealed class SequencingFetcher : DescriptorFetcher
        {
            private readonly AppDescriptor _first;
            private readonly AppDescriptor _rest;
            private int _calls;

            public SequencingFetcher(AppDescriptor first, AppDescriptor rest)
            {
                _first = first;
                _rest = rest;
            }

            public override Task<AppDescriptor> FetchAsync(Uri url, CancellationToken ct) =>
                Task.FromResult(_calls++ == 0 ? _first : _rest);
        }
    }
}
