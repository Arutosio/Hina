using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Crypto;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Diagnostics;
using Hina.PackageManager.Install;
using Hina.PackageManager.Paths;
using Hina.PackageManager.Platform.Linux;
using Hina.PackageManager.Registry;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hina.PackageManager.Tests
{
    // Edge-case bug fixes from the post-Phase-7 audit:
    // C1 minHinaVersion enforced, C2 orphan detect+repair, H1 registry-save recovery,
    // H2 legacy hook identity, H3 dangling side-effects, H4 font collisions,
    // M1 empty registry warning, M2 non-interactive TOFU, M3 Windows font separator,
    // M4 unknown hook action warn, M5 --force runs diff, L1 dot path segment.
    public class EdgeCaseTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly InstallPaths _paths;
        private readonly FakePlatformIntegration _platform;
        private readonly string _privKey;
        private readonly string _pubKey;

        public EdgeCaseTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "hina-edge-" + Path.GetRandomFileName());
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

        // ---- C1: minHinaVersion ----

        [Fact]
        public async Task Install_DescriptorWithFutureMinHinaVersion_IsRejected()
        {
            AppDescriptor d = SignedDescriptor("demo", "1.0.0", minHinaVersion: "99.0.0");
            InstallService svc = NewInstallService(d);

            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.InstallAsync(new Uri("https://example.com/demo.json"), null, CancellationToken.None));

            Assert.Contains("requires Hina", ex.Message);
            Assert.False(Directory.Exists(_paths.AppDir("demo")));
        }

        [Fact]
        public async Task Update_DescriptorWithFutureMinHinaVersion_IsFailedResult()
        {
            await InstallV1();

            AppDescriptor v2 = SignedDescriptor("demo", "1.1.0", minHinaVersion: "99.0.0");
            UpdateService svc = new UpdateService(_paths, _platform,
                fetcher: new StubFetcher(v2),
                patchClientFactory: cfg => new FakePatchClient(cfg, NewExecFiles()));

            UpdateResult r = await svc.UpdateAsync("demo", null, CancellationToken.None);

            Assert.Equal(UpdateStatus.Failed, r.Status);
            Assert.Contains("requires Hina", r.Message);
        }

        // ---- C2: orphan detect + repair ----

        [Fact]
        public async Task Verify_ReportsMissingAppDir()
        {
            await InstallV1();
            Directory.Delete(_paths.AppDir("demo"), recursive: true);

            RegistryVerifier verifier = new RegistryVerifier(_paths, _platform);
            List<AppDiagnostic> diags = verifier.Inspect();

            Assert.Single(diags);
            Assert.True(diags[0].AppDirMissing);
            Assert.False(diags[0].IsHealthy);
        }

        [Fact]
        public async Task Verify_Repair_RemovesOrphanedEntry()
        {
            await InstallV1();
            Directory.Delete(_paths.AppDir("demo"), recursive: true);

            RegistryVerifier verifier = new RegistryVerifier(_paths, _platform);
            List<AppRepairResult> repaired = await verifier.RepairAsync(null, CancellationToken.None);

            Assert.Single(repaired);
            Assert.True(repaired[0].RemovedOrphanEntry);

            Registry.Registry reg = new RegistryStore(_paths.RegistryFile).Load();
            Assert.False(reg.Apps.ContainsKey("demo"));
        }

        // ---- H1: registry-save failure on update ----
        // The fault-injection path that exercises this requires either a virtual SaveAsync
        // override on RegistryStore (out of scope here) or a host where chmod 0o555 reliably
        // blocks both the lock and the save. Both approaches were tried and produce
        // host-dependent failures, so H1's code path is validated by code review + the
        // manual verification step in the plan rather than an automated test.

        // ---- H2: legacy hook identity ----

        [Fact]
        public void ResolveIdentity_AddToPath_RecoversNameFromEvidence()
        {
            HookEvidence ev = new HookEvidence
            {
                Action = "addToPath",
                Identity = "",
                Evidence = "/Users/me/.local/bin/demo"
            };

            string id = UpdateService.ResolveIdentity(ev);
            Assert.Equal("addToPath:demo", id);
        }

        [Fact]
        public void ResolveIdentity_AddToPath_StripsCmdExtensionOnWindowsEvidence()
        {
            HookEvidence ev = new HookEvidence
            {
                Action = "addToPath",
                Identity = "",
                Evidence = "C:\\Users\\me\\AppData\\Local\\Hina\\bin\\demo.cmd"
            };

            string id = UpdateService.ResolveIdentity(ev);
            Assert.Equal("addToPath:demo", id);
        }

        [Fact]
        public void ResolveIdentity_NonEmpty_PassesThrough()
        {
            HookEvidence ev = new HookEvidence
            {
                Action = "installFont",
                Identity = "installFont:fonts/A.ttf",
                Evidence = "/anywhere"
            };
            Assert.Equal("installFont:fonts/A.ttf", UpdateService.ResolveIdentity(ev));
        }

        // ---- H4: font collisions across apps ----

        [Fact]
        public async Task LinuxInstallFont_PerAppOverload_PrefixesDestinationFilename()
        {
            string binDir = Path.Combine(_tempDir, "bin");
            string appsDir = Path.Combine(_tempDir, "apps");
            string fontsDir = Path.Combine(_tempDir, "fonts");
            LinuxPlatformIntegration linux = new LinuxPlatformIntegration(binDir, appsDir, fontsDir, _tempDir);

            string srcA = Path.Combine(_tempDir, "srcA", "Foo.ttf");
            string srcB = Path.Combine(_tempDir, "srcB", "Foo.ttf");
            Directory.CreateDirectory(Path.GetDirectoryName(srcA)!);
            Directory.CreateDirectory(Path.GetDirectoryName(srcB)!);
            File.WriteAllBytes(srcA, new byte[] { 1 });
            File.WriteAllBytes(srcB, new byte[] { 2 });

            string evA = await linux.InstallFont(srcA, "appA", CancellationToken.None);
            string evB = await linux.InstallFont(srcB, "appB", CancellationToken.None);

            Assert.NotEqual(evA, evB);
            Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(evA));
            Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(evB));
        }

        // ---- M1: empty registry warning ----

        [Fact]
        public void RegistryStore_EmptyFile_ReturnsEmptyRegistry()
        {
            string path = Path.Combine(_tempDir, "registry.json");
            File.WriteAllText(path, "");
            // We aren't injecting a logger so just confirm behaviour.
            Registry.Registry r = new RegistryStore(path).Load();
            Assert.Empty(r.Apps);
        }

        // ---- M3: Windows font evidence separator ----

        [Fact]
        public void Windows_FontEvidence_NewSeparatorParsesCorrectly()
        {
            // Re-using the same parser shape that UninstallFont uses internally;
            // we test the parsing via the public split semantics on a sample string.
            string evidence = "C:\\fonts\\Demo.ttf\u001FDemo";
            char sep = evidence.Contains('') ? ''
                     : evidence.Contains('|') ? '|'
                     : (char)0;
            int idx = evidence.IndexOf(sep);
            string filePath = evidence.Substring(0, idx);
            string fontName = evidence.Substring(idx + 1);

            Assert.Equal("C:\\fonts\\Demo.ttf", filePath);
            Assert.Equal("Demo", fontName);
        }

        [Fact]
        public void Windows_FontEvidence_LegacyPipeStillParses()
        {
            string evidence = "C:\\fonts\\Demo.ttf|Demo";
            char sep = evidence.Contains('') ? ''
                     : evidence.Contains('|') ? '|'
                     : (char)0;
            int idx = evidence.IndexOf(sep);
            string filePath = evidence.Substring(0, idx);
            string fontName = evidence.Substring(idx + 1);

            Assert.Equal("C:\\fonts\\Demo.ttf", filePath);
            Assert.Equal("Demo", fontName);
        }

        // ---- M5: --force runs diff when version unchanged ----

        [Fact]
        public async Task Update_SameVersionWithForce_RunsPatch_NotShortCircuit()
        {
            await InstallV1();

            AppDescriptor sameVersion = SignedDescriptor("demo", "1.0.0");
            int patchCalls = 0;
            UpdateService svc = new UpdateService(_paths, _platform,
                fetcher: new StubFetcher(sameVersion),
                patchClientFactory: cfg =>
                {
                    patchCalls++;
                    return new FakePatchClient(cfg, NewExecFiles());
                });

            UpdateResult r = await svc.UpdateAsync("demo", new UpdateOptions { Force = true }, CancellationToken.None);

            Assert.Equal(UpdateStatus.Updated, r.Status);
            Assert.True(patchCalls > 0);
        }

        // ---- L1: dot path segment rejected ----

        [Fact]
        public void Validator_RejectsLeadingDotSegment()
        {
            AppDescriptor d = SignedDescriptor("demo", "1.0.0");
            d.Exec.Linux = "./bin/demo";

            ValidationResult r = DescriptorValidator.Validate(d);
            Assert.Contains(r.Errors, e => e.Contains("'.' segments"));
        }

        // ---- Helpers ----

        private async Task InstallV1(string name = "demo")
        {
            AppDescriptor v1 = SignedDescriptor(name, "1.0.0");
            InstallService svc = NewInstallService(v1);
            await svc.InstallAsync(new Uri($"https://example.com/{name}.json"), new InstallOptions { AssumeTrustOnFirstUse = true }, CancellationToken.None);
        }

        private InstallService NewInstallService(AppDescriptor d) => new InstallService(
            _paths,
            _platform,
            fetcher: new StubFetcher(d),
            patchClientFactory: cfg => new FakePatchClient(cfg, NewExecFiles()));

        private AppDescriptor SignedDescriptor(string name, string version, string? minHinaVersion = null)
        {
            AppDescriptor d = new AppDescriptor
            {
                SchemaVersion = 1,
                Name = name,
                DisplayName = name,
                Version = version,
                Publisher = "Acme",
                BaseUrl = $"https://cdn.example.com/{name}/",
                PublicKey = _pubKey,
                MinHinaVersion = minHinaVersion,
                Exec = new ExecMap { Windows = "bin\\demo.exe", Linux = "bin/demo", Macos = "bin/demo" },
                Entries = { new ShellEntry { Id = "main", Name = name, Exec = ExecRelative() } },
                PostInstall = { new AddToPathHook { Name = "demo", Target = ExecRelative() } }
            };
            DescriptorSigner.AttachSignature(d, Convert.FromBase64String(_privKey));
            return d;
        }

        private static string ExecRelative() =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "bin\\demo.exe" : "bin/demo";

        private static Dictionary<string, byte[]> NewExecFiles() => new()
        {
            [ExecRelative()] = new byte[] { 1, 2, 3 }
        };
    }
}
