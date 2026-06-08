using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Platform.Linux;
using Xunit;

namespace Hina.PackageManager.Tests
{
    // When an app is sandboxed, its .desktop launcher must route through `hina run`
    // (the launchOverride) instead of pointing straight at the app binary — otherwise
    // clicking the icon bypasses the sandbox entirely.
    public class LinuxSandboxShortcutTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly LinuxPlatformIntegration _platform;

        public LinuxSandboxShortcutTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "hina-sbx-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_tempDir);
            _platform = new LinuxPlatformIntegration(
                Path.Combine(_tempDir, "bin"),
                Path.Combine(_tempDir, "apps"),
                Path.Combine(_tempDir, "fonts"),
                Path.Combine(_tempDir, "autostart"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }

        private static async Task<string> ExecLine(string path)
        {
            string[] lines = await File.ReadAllLinesAsync(path);
            return lines.Single(l => l.StartsWith("Exec=", StringComparison.Ordinal));
        }

        private static ShellEntry Entry() => new ShellEntry { Id = "main", Name = "Demo", Exec = "bin/demo" };

        [Fact]
        public async Task LaunchOverride_SetsExecToOverride()
        {
            string path = await _platform.CreateMenuShortcut(Entry(), "/apps/demo", "hina run demo main", CancellationToken.None);
            string exec = await ExecLine(path);
            Assert.Contains("hina run demo main", exec);
            Assert.DoesNotContain("/apps/demo/bin/demo", exec);
        }

        [Fact]
        public async Task NullOverride_PointsAtAppBinary()
        {
            // Path.Combine yields a backslash separator on Windows, breaking the Unix path
            // assertion; the Linux integration only runs on Unix in practice.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            string path = await _platform.CreateMenuShortcut(Entry(), "/apps/demo", null, CancellationToken.None);
            string exec = await ExecLine(path);
            Assert.Contains("/apps/demo/bin/demo", exec);
        }
    }
}
