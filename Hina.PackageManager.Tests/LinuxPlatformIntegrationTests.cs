using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Platform.Linux;

namespace Hina.PackageManager.Tests
{
    // The Linux integration is pure-managed FS code (.desktop files + symlinks). It works the
    // same on macOS — the files are inert there, but creating/removing them is identical, so
    // the same tests verify the implementation on both Unix-like CI runners.
    public class LinuxPlatformIntegrationTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _binDir;
        private readonly string _appsDir;
        private readonly LinuxPlatformIntegration _platform;
        private readonly bool _supportsSymlinks;

        public LinuxPlatformIntegrationTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "hina-linux-" + Path.GetRandomFileName());
            _binDir = Path.Combine(_tempDir, "bin");
            _appsDir = Path.Combine(_tempDir, "apps");
            Directory.CreateDirectory(_tempDir);
            _platform = new LinuxPlatformIntegration(_binDir, _appsDir);
            _supportsSymlinks = !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task CreateMenuShortcut_WritesValidDesktopEntry()
        {
            ShellEntry entry = new ShellEntry
            {
                Id = "main",
                Name = "Foo App",
                Exec = "bin/foo",
                Icon = "icons/foo.png",
                Categories = { "Development", "TextEditor" }
            };
            string appDir = Path.Combine(_tempDir, "apps-payload");
            Directory.CreateDirectory(appDir);

            string evidence = await _platform.CreateMenuShortcut(entry, appDir, CancellationToken.None);

            Assert.True(File.Exists(evidence));
            string content = File.ReadAllText(evidence);
            Assert.Contains("[Desktop Entry]", content);
            Assert.Contains("Type=Application", content);
            Assert.Contains("Name=Foo App", content);
            Assert.Contains($"Exec={Path.Combine(appDir, "bin/foo")}", content);
            Assert.Contains($"Icon={Path.Combine(appDir, "icons/foo.png")}", content);
            Assert.Contains("Categories=Development;TextEditor;", content);
            Assert.Contains("X-Hina-Managed=true", content);
        }

        [Fact]
        public async Task RemoveMenuShortcut_DeletesEvidenceFile_AndIsIdempotent()
        {
            ShellEntry entry = new ShellEntry { Id = "x", Name = "X", Exec = "x" };
            string appDir = Path.Combine(_tempDir, "x-payload");
            Directory.CreateDirectory(appDir);

            string evidence = await _platform.CreateMenuShortcut(entry, appDir, CancellationToken.None);
            await _platform.RemoveMenuShortcut(evidence, CancellationToken.None);
            Assert.False(File.Exists(evidence));

            // Second call is a no-op.
            await _platform.RemoveMenuShortcut(evidence, CancellationToken.None);
        }

        [Fact]
        public async Task AddToPath_CreatesSymlinkAtUserBinDir()
        {
            if (!_supportsSymlinks)
            {
                return;
            }

            string targetExec = Path.Combine(_tempDir, "payload", "bin", "demo");
            Directory.CreateDirectory(Path.GetDirectoryName(targetExec)!);
            File.WriteAllText(targetExec, "#!/bin/sh\necho hi\n");

            string evidence = await _platform.AddToPath("demo", targetExec, CancellationToken.None);

            Assert.Equal(Path.Combine(_binDir, "demo"), evidence);
            // Symlink exists pointing at target.
            FileInfo link = new FileInfo(evidence);
            Assert.NotNull(link.LinkTarget);
        }

        [Fact]
        public async Task AddToPath_OverwritesPreexistingSymlink()
        {
            if (!_supportsSymlinks)
            {
                return;
            }

            string target1 = Path.Combine(_tempDir, "v1.sh");
            string target2 = Path.Combine(_tempDir, "v2.sh");
            File.WriteAllText(target1, "1");
            File.WriteAllText(target2, "2");

            string evidence = await _platform.AddToPath("demo", target1, CancellationToken.None);
            string evidence2 = await _platform.AddToPath("demo", target2, CancellationToken.None);

            Assert.Equal(evidence, evidence2);
            Assert.Equal(target2, new FileInfo(evidence2).LinkTarget);
        }

        [Fact]
        public async Task RemoveFromPath_DeletesSymlink_AndIsIdempotent()
        {
            if (!_supportsSymlinks)
            {
                return;
            }

            string targetExec = Path.Combine(_tempDir, "payload-r.sh");
            File.WriteAllText(targetExec, "x");

            string evidence = await _platform.AddToPath("rem", targetExec, CancellationToken.None);
            await _platform.RemoveFromPath(evidence, CancellationToken.None);
            Assert.False(File.Exists(evidence));

            // Idempotent.
            await _platform.RemoveFromPath(evidence, CancellationToken.None);
        }
    }
}
