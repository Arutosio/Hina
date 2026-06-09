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
        public async Task Uninstall_InstallDirRemovalFails_KeepsRegistryEntry()
        {
            // POSIX-only: we revoke write on the install dir so deleting its contents fails.
            // On Windows there's no equivalent simple, reliable way to force the delete to
            // throw without holding a file handle, so skip there.
            if (OperatingSystem.IsWindows()) return;

            InstallPaths paths = InstallPaths.ForRoot(_root);
            string appDir = Path.Combine(_root, "locked-appdir");
            Directory.CreateDirectory(appDir);
            await File.WriteAllTextAsync(Path.Combine(appDir, "f.txt"), "x");
            await Seed(paths, "demo", appDir);

            // r-x only: the child file can't be unlinked, so Directory.Delete throws.
            File.SetUnixFileMode(appDir,
                UnixFileMode.UserRead | UnixFileMode.UserExecute);

            try
            {
                UninstallResult result = await new UninstallService(paths, new FakePlatformIntegration())
                    .UninstallAsync("demo", CancellationToken.None);

                // The delete failed, so the dir is still on disk...
                Assert.True(Directory.Exists(appDir));
                // ...and the app must remain registered (not silently stranded as orphaned
                // files with no registry pointer). Removal reported as not done.
                Assert.False(result.Removed);
                Assert.True(new RegistryStore(paths.RegistryFile).Load().Apps.ContainsKey("demo"));
            }
            finally
            {
                // Restore perms so the test root can be cleaned up.
                File.SetUnixFileMode(appDir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
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
