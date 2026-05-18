using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Hina.PackageManager.Paths
{
    // Per-OS install layout. Single source of truth for every disk location Hina writes to.
    public sealed class InstallPaths
    {
        public string RootDir { get; }
        public string AppsRoot { get; }
        public string RegistryFile { get; }
        public string DescriptorCacheRoot { get; }
        public string UserBinDir { get; }

        private InstallPaths(string rootDir, string appsRoot, string registryFile, string descriptorCacheRoot, string userBinDir)
        {
            RootDir = rootDir;
            AppsRoot = appsRoot;
            RegistryFile = registryFile;
            DescriptorCacheRoot = descriptorCacheRoot;
            UserBinDir = userBinDir;
        }

        public string AppDir(string name) => Path.Combine(AppsRoot, name);

        public string DescriptorCache(string name) => Path.Combine(DescriptorCacheRoot, name + ".json");

        public string LockFile => RegistryFile + ".lock";

        public void EnsureRootDirs()
        {
            Directory.CreateDirectory(RootDir);
            Directory.CreateDirectory(AppsRoot);
            Directory.CreateDirectory(DescriptorCacheRoot);
            Directory.CreateDirectory(UserBinDir);
        }

        // Production paths derived from OS conventions. Per-user, no admin required.
        public static InstallPaths ForCurrentOs()
        {
            string root;
            string userBin;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                root = Path.Combine(localAppData, "Hina");
                userBin = Path.Combine(root, "bin");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                root = Path.Combine(home, "Library", "Application Support", "Hina");
                userBin = Path.Combine(home, ".local", "bin");
            }
            else
            {
                string xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                                 ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
                root = Path.Combine(xdgData, "hina");
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                userBin = Path.Combine(home, ".local", "bin");
            }

            return new InstallPaths(
                rootDir: root,
                appsRoot: Path.Combine(root, "Apps"),
                registryFile: Path.Combine(root, "registry.json"),
                descriptorCacheRoot: Path.Combine(root, "descriptors"),
                userBinDir: userBin);
        }

        // Test seam: build paths under an arbitrary root (used by integration tests so we don't pollute the dev machine).
        public static InstallPaths ForRoot(string root, string? userBinOverride = null)
        {
            return new InstallPaths(
                rootDir: root,
                appsRoot: Path.Combine(root, "Apps"),
                registryFile: Path.Combine(root, "registry.json"),
                descriptorCacheRoot: Path.Combine(root, "descriptors"),
                userBinDir: userBinOverride ?? Path.Combine(root, "bin"));
        }
    }
}
