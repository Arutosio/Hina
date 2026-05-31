using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Platform.Linux;
using Xunit;

namespace Hina.PackageManager.Tests
{
    // Sec HIGH defense-in-depth: even if a control char reaches the Linux writer (validator
    // bypassed / future field), the generated .desktop file must not gain an injected key line.
    public class LinuxDesktopInjectionTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly LinuxPlatformIntegration _platform;

        public LinuxDesktopInjectionTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "hina-inject-" + Path.GetRandomFileName());
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

        [Fact]
        public async Task CreateMenuShortcut_CategoryWithNewline_NoInjectedKey()
        {
            ShellEntry entry = new ShellEntry
            {
                Id = "main",
                Name = "Demo",
                Exec = "bin/demo",
                Categories = { "Utility\nExec=/bin/sh -c evil" }
            };
            string path = await _platform.CreateMenuShortcut(entry, _tempDir, CancellationToken.None);

            string[] lines = await File.ReadAllLinesAsync(path);
            // Exactly one Exec= line (the legitimate one); the injected one must be gone.
            Assert.Equal(1, lines.Count(l => l.StartsWith("Exec=", StringComparison.Ordinal)));
            Assert.DoesNotContain(lines, l => l.Contains("/bin/sh -c evil", StringComparison.Ordinal) && l.StartsWith("Exec="));
        }

        [Fact]
        public async Task RegisterAutostart_EntryIdWithNewline_NoInjectedKey()
        {
            AutostartHook hook = new AutostartHook { EntryId = "main\nExec=/bin/sh -c evil" };
            string path = await _platform.RegisterAutostart(hook, _tempDir, CancellationToken.None);

            string[] lines = await File.ReadAllLinesAsync(path);
            Assert.Equal(1, lines.Count(l => l.StartsWith("Exec=", StringComparison.Ordinal)));
        }
    }
}
