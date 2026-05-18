using System.IO;
using Hina.PackageManager.Paths;

namespace Hina.PackageManager.Tests
{
    public class InstallPathsTests
    {
        [Fact]
        public void ForRoot_DerivesAllSubpaths()
        {
            string root = Path.Combine(Path.GetTempPath(), "hina-paths-" + Path.GetRandomFileName());

            InstallPaths p = InstallPaths.ForRoot(root);

            Assert.Equal(root, p.RootDir);
            Assert.Equal(Path.Combine(root, "Apps"), p.AppsRoot);
            Assert.Equal(Path.Combine(root, "registry.json"), p.RegistryFile);
            Assert.Equal(Path.Combine(root, "registry.json.lock"), p.LockFile);
            Assert.Equal(Path.Combine(root, "descriptors", "foo.json"), p.DescriptorCache("foo"));
            Assert.Equal(Path.Combine(root, "Apps", "foo"), p.AppDir("foo"));
        }

        [Fact]
        public void EnsureRootDirs_CreatesAllDirectories()
        {
            string root = Path.Combine(Path.GetTempPath(), "hina-paths-" + Path.GetRandomFileName());
            try
            {
                InstallPaths p = InstallPaths.ForRoot(root);
                p.EnsureRootDirs();

                Assert.True(Directory.Exists(p.RootDir));
                Assert.True(Directory.Exists(p.AppsRoot));
                Assert.True(Directory.Exists(p.DescriptorCacheRoot));
                Assert.True(Directory.Exists(p.UserBinDir));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        [Fact]
        public void ForCurrentOs_ProducesNonEmptyConsistentPaths()
        {
            InstallPaths p = InstallPaths.ForCurrentOs();

            Assert.False(string.IsNullOrEmpty(p.RootDir));
            Assert.StartsWith(p.RootDir, p.AppsRoot);
            Assert.StartsWith(p.RootDir, p.RegistryFile);
            Assert.StartsWith(p.RootDir, p.DescriptorCacheRoot);
        }
    }
}
