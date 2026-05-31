using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Platform.Linux;

namespace Hina.PackageManager.Tests
{
    // Phase 4 hooks: MIME, URL scheme, font, autostart. Cross-OS-safe because the impl
    // writes plain files / .desktop entries with no shell-outs.
    public class LinuxPlatformIntegrationPhase4Tests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _binDir;
        private readonly string _appsDir;
        private readonly string _fontsDir;
        private readonly string _autostartDir;
        private readonly LinuxPlatformIntegration _platform;

        public LinuxPlatformIntegrationPhase4Tests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "hina-linux4-" + Path.GetRandomFileName());
            _binDir = Path.Combine(_tempDir, "bin");
            _appsDir = Path.Combine(_tempDir, "apps");
            _fontsDir = Path.Combine(_tempDir, "fonts");
            _autostartDir = Path.Combine(_tempDir, "autostart");
            Directory.CreateDirectory(_tempDir);
            _platform = new LinuxPlatformIntegration(_binDir, _appsDir, _fontsDir, _autostartDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task RegisterMimeType_WritesDesktopFileWithMimeLine()
        {
            MimeTypeHook hook = new MimeTypeHook
            {
                MimeType = "application/x-fooedit",
                Extensions = { ".foo", ".bar" },
                EntryId = "main"
            };

            string evidence = await _platform.RegisterMimeType(hook, "/apps/foo", null, CancellationToken.None);

            Assert.True(File.Exists(evidence));
            Assert.Equal(_appsDir, Path.GetDirectoryName(evidence));

            string content = File.ReadAllText(evidence);
            Assert.Contains("[Desktop Entry]", content);
            Assert.Contains("MimeType=application/x-fooedit;", content);
            Assert.Contains("X-Hina-Mime-Extensions=.foo,.bar", content);
            Assert.Contains("NoDisplay=true", content);
            Assert.Contains("X-Hina-Managed=true", content);
        }

        [Fact]
        public async Task UnregisterMimeType_DeletesFile_AndIsIdempotent()
        {
            MimeTypeHook hook = new MimeTypeHook { MimeType = "application/x-x", Extensions = { ".x" }, EntryId = "main" };
            string evidence = await _platform.RegisterMimeType(hook, "/apps/x", null, CancellationToken.None);

            await _platform.UnregisterMimeType(evidence, CancellationToken.None);
            Assert.False(File.Exists(evidence));

            // Idempotent.
            await _platform.UnregisterMimeType(evidence, CancellationToken.None);
        }

        [Fact]
        public async Task RegisterUrlScheme_UsesXSchemeHandlerMime()
        {
            UrlSchemeHook hook = new UrlSchemeHook { Scheme = "fooedit", EntryId = "main" };

            string evidence = await _platform.RegisterUrlScheme(hook, "/apps/foo", null, CancellationToken.None);

            string content = File.ReadAllText(evidence);
            Assert.Contains("MimeType=x-scheme-handler/fooedit;", content);
            Assert.Contains("NoDisplay=true", content);
        }

        [Fact]
        public async Task UnregisterUrlScheme_DeletesFile_AndIsIdempotent()
        {
            UrlSchemeHook hook = new UrlSchemeHook { Scheme = "rmtest", EntryId = "main" };
            string evidence = await _platform.RegisterUrlScheme(hook, "/apps/x", null, CancellationToken.None);

            await _platform.UnregisterUrlScheme(evidence, CancellationToken.None);
            Assert.False(File.Exists(evidence));

            await _platform.UnregisterUrlScheme(evidence, CancellationToken.None);
        }

        [Fact]
        public async Task InstallFont_CopiesFileIntoFontsDir()
        {
            string src = Path.Combine(_tempDir, "src", "Foo.ttf");
            Directory.CreateDirectory(Path.GetDirectoryName(src)!);
            byte[] payload = new byte[] { 0x00, 0x01, 0x00, 0x00, 0xDE, 0xAD };  // not a real font; just bytes to verify the copy
            File.WriteAllBytes(src, payload);

            string evidence = await _platform.InstallFont(src, CancellationToken.None);

            Assert.Equal(Path.Combine(_fontsDir, "Foo.ttf"), evidence);
            Assert.True(File.Exists(evidence));
            Assert.Equal(payload, File.ReadAllBytes(evidence));
        }

        [Fact]
        public async Task UninstallFont_DeletesEvidenceFile_AndIsIdempotent()
        {
            string src = Path.Combine(_tempDir, "src2", "Bar.otf");
            Directory.CreateDirectory(Path.GetDirectoryName(src)!);
            File.WriteAllBytes(src, new byte[] { 1, 2 });

            string evidence = await _platform.InstallFont(src, CancellationToken.None);
            Assert.True(File.Exists(evidence));

            await _platform.UninstallFont(evidence, CancellationToken.None);
            Assert.False(File.Exists(evidence));

            await _platform.UninstallFont(evidence, CancellationToken.None);
        }

        [Fact]
        public async Task RegisterAutostart_WritesDesktopFileInAutostartDir()
        {
            AutostartHook hook = new AutostartHook
            {
                EntryId = "main",
                Args = new() { "--minimized" }
            };

            string evidence = await _platform.RegisterAutostart(hook, "/apps/foo", null, CancellationToken.None);

            Assert.Equal(_autostartDir, Path.GetDirectoryName(evidence));
            string content = File.ReadAllText(evidence);
            Assert.Contains("[Desktop Entry]", content);
            Assert.Contains("X-GNOME-Autostart-enabled=true", content);
            Assert.Contains("--minimized", content);
            Assert.Contains("X-Hina-Managed=true", content);
        }

        [Fact]
        public async Task RegisterAutostart_WithResolvedExec_WritesExecLineWithPath()
        {
            AutostartHook hook = new AutostartHook { EntryId = "main", Args = new() { "--minimized" } };
            string execAbs = "/apps/foo/bin/app";

            string evidence = await _platform.RegisterAutostart(hook, "/apps/foo", execAbs, CancellationToken.None);

            string content = File.ReadAllText(evidence);
            Assert.Contains("Exec=", content);
            Assert.Contains(execAbs, content); // the .desktop now actually launches the app
            Assert.DoesNotContain("Exec= ", content); // not an empty exec
        }

        [Fact]
        public async Task UnregisterAutostart_DeletesFile_AndIsIdempotent()
        {
            AutostartHook hook = new AutostartHook { EntryId = "rm" };
            string evidence = await _platform.RegisterAutostart(hook, "/apps/x", null, CancellationToken.None);

            await _platform.UnregisterAutostart(evidence, CancellationToken.None);
            Assert.False(File.Exists(evidence));

            await _platform.UnregisterAutostart(evidence, CancellationToken.None);
        }
    }
}
