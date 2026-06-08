using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Diagnostics;
using Hina.PackageManager.Paths;
using Hina.PackageManager.Platform.Linux;
using Hina.PackageManager.Registry;
using Xunit;

namespace Hina.PackageManager.Tests
{
    // `hina repair` orphan scan: artifacts left on disk after a manual registry deletion —
    // a hina-*.desktop with no registry row referencing it — are detected and removed, while
    // referenced ones are left alone.
    public class OrphanArtifactsTests : IDisposable
    {
        private readonly string _root;
        private readonly string _appsDir;
        private readonly InstallPaths _paths;
        private readonly LinuxPlatformIntegration _platform;

        public OrphanArtifactsTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "hina-orphan-" + Path.GetRandomFileName());
            _appsDir = Path.Combine(_root, "applications");
            Directory.CreateDirectory(_appsDir);
            _paths = InstallPaths.ForRoot(_root);
            _platform = new LinuxPlatformIntegration(
                Path.Combine(_root, "bin"), _appsDir,
                Path.Combine(_root, "fonts"), Path.Combine(_root, "autostart"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private string MakeDesktop(string name)
        {
            string p = Path.Combine(_appsDir, name);
            File.WriteAllText(p, "[Desktop Entry]\nX-Hina-Managed=true\n");
            return p;
        }

        private void SeedRegistryReferencing(string referencedDesktopPath)
        {
            Registry.Registry reg = new Registry.Registry();
            reg.Apps["demo"] = new InstalledApp
            {
                Name = "demo",
                InstallPath = _root, // exists
                ShellEntries = { new ShellEntryRecord { Id = "main", Evidence = referencedDesktopPath } },
            };
            new RegistryStore(_paths.RegistryFile).SaveAsync(reg).GetAwaiter().GetResult();
        }

        [Fact]
        public void FindOrphanArtifacts_DetectsOnlyUnreferenced()
        {
            string referenced = MakeDesktop("hina-main.desktop");
            string orphan = MakeDesktop("hina-ghost.desktop");
            SeedRegistryReferencing(referenced);

            List<string> orphans = new RegistryVerifier(_paths, _platform).FindOrphanArtifacts();

            Assert.Contains(orphan, orphans);
            Assert.DoesNotContain(referenced, orphans);
        }

        [Fact]
        public async Task RepairOrphanArtifacts_RemovesOrphan_KeepsReferenced()
        {
            string referenced = MakeDesktop("hina-main.desktop");
            string orphan = MakeDesktop("hina-ghost.desktop");
            SeedRegistryReferencing(referenced);

            List<string> removed = await new RegistryVerifier(_paths, _platform).RepairOrphanArtifactsAsync(CancellationToken.None);

            Assert.Contains(orphan, removed);
            Assert.False(File.Exists(orphan), "orphan must be deleted");
            Assert.True(File.Exists(referenced), "referenced artifact must survive");
        }

        [Fact]
        public void FindOrphanArtifacts_IgnoresNonHinaFiles()
        {
            File.WriteAllText(Path.Combine(_appsDir, "firefox.desktop"), "[Desktop Entry]\n");
            SeedRegistryReferencing(MakeDesktop("hina-main.desktop"));

            List<string> orphans = new RegistryVerifier(_paths, _platform).FindOrphanArtifacts();

            Assert.DoesNotContain(orphans, p => p.EndsWith("firefox.desktop", StringComparison.Ordinal));
        }
    }
}
