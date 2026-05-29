using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Install;
using Hina.PackageManager.Paths;
using Hina.PackageManager.Registry;

namespace Hina.PackageManager.Tests
{
    public class UninstallServiceTests : IDisposable
    {
        private readonly string _root;

        public UninstallServiceTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "hina-uninst-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private async Task Seed(InstallPaths paths, string name, string installPath)
        {
            Registry.Registry reg = new Registry.Registry();
            reg.Apps[name] = new InstalledApp
            {
                Name = name,
                InstalledVersion = "1.0.0",
                InstallPath = installPath
            };
            await new RegistryStore(paths.RegistryFile).SaveAsync(reg);
        }

        [Fact]
        public async Task Uninstall_RegularDirectory_IsDeleted()
        {
            InstallPaths paths = InstallPaths.ForRoot(_root);
            string appDir = Path.Combine(_root, "appdir");
            Directory.CreateDirectory(appDir);
            await File.WriteAllTextAsync(Path.Combine(appDir, "f.txt"), "x");
            await Seed(paths, "demo", appDir);

            UninstallResult result = await new UninstallService(paths, new FakePlatformIntegration())
                .UninstallAsync("demo", CancellationToken.None);

            Assert.True(result.Removed);
            Assert.False(Directory.Exists(appDir));
            Assert.False(new RegistryStore(paths.RegistryFile).Load().Apps.ContainsKey("demo"));
        }

        [Fact]
        public async Task Uninstall_InstallPathIsDirectorySymlink_RemovesLinkNotTarget()
        {
            InstallPaths paths = InstallPaths.ForRoot(_root);

            // Real user data the symlink points at — must survive uninstall.
            string target = Path.Combine(_root, "real-data");
            Directory.CreateDirectory(target);
            string precious = Path.Combine(target, "precious.txt");
            await File.WriteAllTextAsync(precious, "do not delete");

            string link = Path.Combine(_root, "linked-install");
            Directory.CreateSymbolicLink(link, target);

            await Seed(paths, "demo", link);

            UninstallResult result = await new UninstallService(paths, new FakePlatformIntegration())
                .UninstallAsync("demo", CancellationToken.None);

            Assert.True(result.Removed);
            // The symlink itself is gone...
            Assert.False(Directory.Exists(link));
            // ...but the target directory and its contents are untouched.
            Assert.True(Directory.Exists(target));
            Assert.True(File.Exists(precious));
            Assert.Equal("do not delete", await File.ReadAllTextAsync(precious));
        }
    }
}
