using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Platform.MacOS;

namespace Hina.PackageManager.Tests
{
    // MacOSPlatformIntegration is pure-file-write — bundles, plists, symlinks. Tests
    // run on Linux + macOS the same way, rooted at TempDir.
    public class MacOSPlatformIntegrationTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _binDir;
        private readonly string _appsDir;
        private readonly string _fontsDir;
        private readonly string _launchAgentsDir;
        private readonly MacOSPlatformIntegration _platform;
        private readonly bool _supportsSymlinks;

        public MacOSPlatformIntegrationTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "hina-macos-" + Path.GetRandomFileName());
            _binDir = Path.Combine(_tempDir, "bin");
            _appsDir = Path.Combine(_tempDir, "Applications");
            _fontsDir = Path.Combine(_tempDir, "Fonts");
            _launchAgentsDir = Path.Combine(_tempDir, "LaunchAgents");
            Directory.CreateDirectory(_tempDir);
            _platform = new MacOSPlatformIntegration(_binDir, _appsDir, _fontsDir, _launchAgentsDir);
            _supportsSymlinks = !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task CreateMenuShortcut_WritesAppBundleWithInfoPlist()
        {
            if (!_supportsSymlinks) return;

            string appDir = Path.Combine(_tempDir, "payload");
            string execAbs = Path.Combine(appDir, "bin", "demo");
            Directory.CreateDirectory(Path.GetDirectoryName(execAbs)!);
            File.WriteAllText(execAbs, "#!/bin/sh\necho hi\n");

            ShellEntry entry = new ShellEntry { Id = "main", Name = "Demo App", Exec = "bin/demo" };
            string bundlePath = await _platform.CreateMenuShortcut(entry, appDir, CancellationToken.None);

            Assert.EndsWith(".app", bundlePath);
            Assert.True(Directory.Exists(bundlePath));
            Assert.True(File.Exists(Path.Combine(bundlePath, "Contents", "Info.plist")));

            string plist = File.ReadAllText(Path.Combine(bundlePath, "Contents", "Info.plist"));
            Assert.Contains("<key>CFBundleName</key>", plist);
            // CFBundleName must be the human-readable display name, not the id-suffixed stem.
            Assert.Contains("<string>Demo App</string>", plist);
            Assert.Contains("<key>CFBundleExecutable</key>", plist);
            Assert.Contains("X-Hina-Managed", plist);

            // Bundle's MacOS/<exec> symlinks to the real exec.
            // The exec filename is derived from the unique bundleName ("Demo App main"),
            // not the display name, so Finder still sees "Demo App" via CFBundleName.
            string bundleExec = Path.Combine(bundlePath, "Contents", "MacOS", "Demo App main");
            Assert.NotNull(new FileInfo(bundleExec).LinkTarget);
        }

        [Fact]
        public async Task CreateMenuShortcut_WithLaunchOverride_WritesScriptStubNotSymlink()
        {
            if (!_supportsSymlinks) return;

            string appDir = Path.Combine(_tempDir, "sandboxed");
            string execAbs = Path.Combine(appDir, "bin", "demo");
            Directory.CreateDirectory(Path.GetDirectoryName(execAbs)!);
            File.WriteAllText(execAbs, "#!/bin/sh\necho hi\n");

            ShellEntry entry = new ShellEntry { Id = "main", Name = "Boxed App", Exec = "bin/demo" };
            string launchOverride = "\"/usr/local/bin/hina\" run boxed \"main\"";
            string bundlePath = await _platform.CreateMenuShortcut(entry, appDir, launchOverride, CancellationToken.None);

            // The exec filename is derived from the unique bundleName ("Boxed App main"),
            // not the display name.
            string bundleExec = Path.Combine(bundlePath, "Contents", "MacOS", "Boxed App main");
            // A sandboxed app must launch via `hina run`, so the bundle exec is a
            // script that invokes the override — NOT a symlink to the raw binary
            // (which would bypass the sandbox).
            Assert.Null(new FileInfo(bundleExec).LinkTarget);
            string stub = File.ReadAllText(bundleExec);
            Assert.Contains("hina", stub);
            Assert.Contains("run boxed", stub);
        }

        [Fact]
        public async Task RemoveMenuShortcut_DeletesBundleRecursively_AndIsIdempotent()
        {
            if (!_supportsSymlinks) return;

            string appDir = Path.Combine(_tempDir, "payload2");
            string execAbs = Path.Combine(appDir, "exec");
            Directory.CreateDirectory(appDir);
            File.WriteAllText(execAbs, "x");

            string bundlePath = await _platform.CreateMenuShortcut(
                new ShellEntry { Id = "x", Name = "Removable", Exec = "exec" },
                appDir,
                CancellationToken.None);

            await _platform.RemoveMenuShortcut(bundlePath, CancellationToken.None);
            Assert.False(Directory.Exists(bundlePath));

            await _platform.RemoveMenuShortcut(bundlePath, CancellationToken.None);
        }

        [Fact]
        public async Task AddToPath_CreatesSymlinkInUserBinDir()
        {
            if (!_supportsSymlinks) return;

            string targetExec = Path.Combine(_tempDir, "src", "demo");
            Directory.CreateDirectory(Path.GetDirectoryName(targetExec)!);
            File.WriteAllText(targetExec, "x");

            string evidence = await _platform.AddToPath("demo", targetExec, CancellationToken.None);

            Assert.Equal(Path.Combine(_binDir, "demo"), evidence);
            Assert.Equal(targetExec, new FileInfo(evidence).LinkTarget);
        }

        [Fact]
        public async Task RegisterMimeType_WritesHelperBundleWithDocumentTypes()
        {
            string bundlePath = await _platform.RegisterMimeType(
                new MimeTypeHook { MimeType = "application/x-demo", Extensions = { ".demo" }, EntryId = "main" },
                "/apps/demo",
                null,
                CancellationToken.None);

            Assert.EndsWith(".app", bundlePath);
            string plist = File.ReadAllText(Path.Combine(bundlePath, "Contents", "Info.plist"));
            Assert.Contains("<key>CFBundleDocumentTypes</key>", plist);
            Assert.Contains("application/x-demo", plist);
            Assert.Contains("<string>demo</string>", plist);  // extension without leading dot
        }

        [Fact]
        public async Task RegisterUrlScheme_WritesHelperBundleWithUrlTypes()
        {
            string bundlePath = await _platform.RegisterUrlScheme(
                new UrlSchemeHook { Scheme = "demoapp", EntryId = "main" },
                "/apps/demo",
                null,
                CancellationToken.None);

            string plist = File.ReadAllText(Path.Combine(bundlePath, "Contents", "Info.plist"));
            Assert.Contains("<key>CFBundleURLTypes</key>", plist);
            Assert.Contains("<string>demoapp</string>", plist);
        }

        [Fact]
        public async Task InstallFont_CopiesFileIntoFontsDir()
        {
            string src = Path.Combine(_tempDir, "src", "Foo.ttf");
            Directory.CreateDirectory(Path.GetDirectoryName(src)!);
            byte[] payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
            File.WriteAllBytes(src, payload);

            string evidence = await _platform.InstallFont(src, CancellationToken.None);

            Assert.Equal(Path.Combine(_fontsDir, "Foo.ttf"), evidence);
            Assert.Equal(payload, File.ReadAllBytes(evidence));
        }

        [Fact]
        public async Task RegisterAutostart_WritesLaunchAgentPlistWithRunAtLoad()
        {
            string evidence = await _platform.RegisterAutostart(
                new AutostartHook { EntryId = "demo-daemon", Args = new() { "--quiet" } },
                "/apps/demo",
                null,
                CancellationToken.None);

            Assert.Equal(_launchAgentsDir, Path.GetDirectoryName(evidence));
            string plist = File.ReadAllText(evidence);
            Assert.Contains("<key>Label</key>", plist);
            Assert.Contains("com.hina.autostart.demo-daemon", plist);
            Assert.Contains("<key>RunAtLoad</key>", plist);
            Assert.Contains("<true/>", plist);
            Assert.Contains("<string>--quiet</string>", plist);
        }

        [Fact]
        public async Task UnregisterAutostart_DeletesPlist_AndIsIdempotent()
        {
            string evidence = await _platform.RegisterAutostart(
                new AutostartHook { EntryId = "rm" }, "/apps/x", null, CancellationToken.None);

            await _platform.UnregisterAutostart(evidence, CancellationToken.None);
            Assert.False(File.Exists(evidence));

            await _platform.UnregisterAutostart(evidence, CancellationToken.None);
        }

        // ---- BUG-016 regression: two entries with the same Name must produce distinct bundle paths ----

        [Fact]
        public async Task CreateMenuShortcut_SameName_DifferentId_ProducesDistinctBundlePaths()
        {
            if (!_supportsSymlinks) return;

            string appDir = Path.Combine(_tempDir, "payload-samename");
            string execAbs = Path.Combine(appDir, "bin", "demo");
            Directory.CreateDirectory(Path.GetDirectoryName(execAbs)!);
            File.WriteAllText(execAbs, "#!/bin/sh\necho hi\n");

            // Two entries share the same Name but have different Ids.
            ShellEntry entryA = new ShellEntry { Id = "entry-a", Name = "My App", Exec = "bin/demo" };
            ShellEntry entryB = new ShellEntry { Id = "entry-b", Name = "My App", Exec = "bin/demo" };

            string pathA = await _platform.CreateMenuShortcut(entryA, appDir, CancellationToken.None);
            string pathB = await _platform.CreateMenuShortcut(entryB, appDir, CancellationToken.None);

            // Paths must be distinct — no clobbering.
            Assert.NotEqual(pathA, pathB);
            Assert.True(Directory.Exists(pathA));
            Assert.True(Directory.Exists(pathB));
        }

        [Fact]
        public async Task CreateMenuShortcut_SameName_DifferentId_RemovingOneDoesNotAffectOther()
        {
            if (!_supportsSymlinks) return;

            string appDir = Path.Combine(_tempDir, "payload-samename-remove");
            string execAbs = Path.Combine(appDir, "bin", "demo");
            Directory.CreateDirectory(Path.GetDirectoryName(execAbs)!);
            File.WriteAllText(execAbs, "#!/bin/sh\necho hi\n");

            ShellEntry entryA = new ShellEntry { Id = "alpha", Name = "Shared Name", Exec = "bin/demo" };
            ShellEntry entryB = new ShellEntry { Id = "beta",  Name = "Shared Name", Exec = "bin/demo" };

            string pathA = await _platform.CreateMenuShortcut(entryA, appDir, CancellationToken.None);
            string pathB = await _platform.CreateMenuShortcut(entryB, appDir, CancellationToken.None);

            // Removing A must leave B untouched.
            await _platform.RemoveMenuShortcut(pathA, CancellationToken.None);
            Assert.False(Directory.Exists(pathA));
            Assert.True(Directory.Exists(pathB));
        }

        [Fact]
        public async Task CreateMenuShortcut_PlistCFBundleName_IsDisplayNameNotStem()
        {
            if (!_supportsSymlinks) return;

            string appDir = Path.Combine(_tempDir, "payload-displayname");
            string execAbs = Path.Combine(appDir, "bin", "demo");
            Directory.CreateDirectory(Path.GetDirectoryName(execAbs)!);
            File.WriteAllText(execAbs, "#!/bin/sh\necho hi\n");

            ShellEntry entry = new ShellEntry { Id = "my-id", Name = "Pretty Name", Exec = "bin/demo" };
            string bundlePath = await _platform.CreateMenuShortcut(entry, appDir, CancellationToken.None);

            string plist = File.ReadAllText(Path.Combine(bundlePath, "Contents", "Info.plist"));
            // CFBundleName must be the human-readable display name, not the id-suffixed unique stem.
            Assert.Contains("<string>Pretty Name</string>", plist);
            // The id suffix must NOT appear in CFBundleName.
            Assert.DoesNotContain("<string>Pretty Name my-id</string>", plist);
        }

        // ---- BUG-007 regression: MIME/URL handler bundles must not collide when two entries share the same type/scheme ----

        [Fact]
        public async Task RegisterMimeType_SameMime_DifferentEntry_ProducesDistinctBundlePaths()
        {
            MimeTypeHook hookA = new MimeTypeHook { MimeType = "application/x-shared", Extensions = { ".shared" }, EntryId = "entry-a" };
            MimeTypeHook hookB = new MimeTypeHook { MimeType = "application/x-shared", Extensions = { ".shared" }, EntryId = "entry-b" };

            string pathA = await _platform.RegisterMimeType(hookA, "/apps/demo", null, CancellationToken.None);
            string pathB = await _platform.RegisterMimeType(hookB, "/apps/demo", null, CancellationToken.None);

            Assert.NotEqual(pathA, pathB);
            Assert.True(Directory.Exists(pathA));
            Assert.True(Directory.Exists(pathB));
        }

        [Fact]
        public async Task RegisterUrlScheme_SameScheme_DifferentEntry_ProducesDistinctBundlePaths()
        {
            UrlSchemeHook hookA = new UrlSchemeHook { Scheme = "myapp", EntryId = "entry-a" };
            UrlSchemeHook hookB = new UrlSchemeHook { Scheme = "myapp", EntryId = "entry-b" };

            string pathA = await _platform.RegisterUrlScheme(hookA, "/apps/demo", null, CancellationToken.None);
            string pathB = await _platform.RegisterUrlScheme(hookB, "/apps/demo", null, CancellationToken.None);

            Assert.NotEqual(pathA, pathB);
            Assert.True(Directory.Exists(pathA));
            Assert.True(Directory.Exists(pathB));
        }
    }
}
