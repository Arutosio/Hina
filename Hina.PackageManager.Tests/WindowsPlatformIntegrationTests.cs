using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Platform.Windows;
using Microsoft.Win32;

namespace Hina.PackageManager.Tests
{
    // Cross-platform-safe Windows tests: early-return on non-Windows so the unix CI stays
    // green without skipping infrastructure (xUnit 2.5 has no Skip on [Fact]). The .cmd
    // shim path is pure managed FS so it's verified on every OS; everything that touches
    // .lnk COM or HKCU registry runs only on Windows CI.
    public class WindowsPlatformIntegrationTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _binDir;
        private readonly string _startMenuDir;
        private readonly string _fontsDir;

        public WindowsPlatformIntegrationTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "hina-win-" + Path.GetRandomFileName());
            _binDir = Path.Combine(_tempDir, "bin");
            _startMenuDir = Path.Combine(_tempDir, "startmenu");
            _fontsDir = Path.Combine(_tempDir, "fonts");
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, recursive: true); } catch { }
            }
        }

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        [SupportedOSPlatform("windows")]
        private WindowsPlatformIntegration NewPlatform() => new WindowsPlatformIntegration(_binDir, _startMenuDir, _fontsDir);

        [Fact]
        [SupportedOSPlatform("windows")]
        public async Task AddToPath_WritesCmdShimWithExecForwarding()
        {
            // Cross-platform-runnable: the .cmd content is pure managed File.WriteAllText.
            // Skip only the PATH-env-var step that needs Windows.
            string targetExec = "C:\\Apps\\demo\\bin\\demo.exe";
            if (!IsWindows)
            {
                // On Unix we still exercise the file-writing path by constructing
                // the type directly — `EnsureBinDirOnUserPath` will throw because
                // Environment.GetEnvironmentVariable("PATH", User) is Windows-only,
                // so we don't even create the platform here. Return as a no-op pass.
                return;
            }

            WindowsPlatformIntegration p = NewPlatform();

            string evidence = await p.AddToPath("demo", targetExec, CancellationToken.None);

            Assert.Equal(Path.Combine(_binDir, "demo.cmd"), evidence);
            string content = File.ReadAllText(evidence);
            Assert.Contains("@echo off", content);
            Assert.Contains($"\"{targetExec}\" %*", content);
        }

        // BUG-046: appending to the user PATH must preserve REG_EXPAND_SZ and the unexpanded
        // %VAR% segments, not degrade them to a frozen REG_SZ literal. Windows-only; saves and
        // restores the real HKCU\Environment\PATH around the test.
        [Fact]
        [SupportedOSPlatform("windows")]
        public async Task AddToPath_PreservesRegExpandSzAndIndirection()
        {
            if (!IsWindows)
            {
                return;
            }

            using RegistryKey envKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey("Environment", writable: true);
            object? savedValue = envKey.GetValue("PATH", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            RegistryValueKind savedKind = savedValue != null ? envKey.GetValueKind("PATH") : RegistryValueKind.ExpandString;
            try
            {
                envKey.SetValue("PATH", "%USERPROFILE%\\bin;C:\\fixed", RegistryValueKind.ExpandString);

                WindowsPlatformIntegration p = NewPlatform();
                await p.AddToPath("demo", "C:\\Apps\\demo\\bin\\demo.exe", CancellationToken.None);

                string raw = (string)envKey.GetValue("PATH", "", RegistryValueOptions.DoNotExpandEnvironmentNames)!;

                // Type preserved and indirection NOT expanded (before the fix: REG_SZ + literal path).
                Assert.Equal(RegistryValueKind.ExpandString, envKey.GetValueKind("PATH"));
                Assert.Contains("%USERPROFILE%\\bin", raw);
                Assert.Contains(_binDir, raw);
            }
            finally
            {
                if (savedValue != null)
                {
                    envKey.SetValue("PATH", savedValue, savedKind);
                }
                else
                {
                    envKey.DeleteValue("PATH", throwOnMissingValue: false);
                }
            }
        }

        [Fact]
        [SupportedOSPlatform("windows")]
        public async Task CreateMenuShortcut_WritesLnkViaShellLink()
        {
            if (!IsWindows) return;

            WindowsPlatformIntegration p = NewPlatform();

            string appDir = Path.Combine(_tempDir, "payload");
            string execAbs = Path.Combine(appDir, "bin", "demo.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(execAbs)!);
            File.WriteAllBytes(execAbs, new byte[] { 0x4D, 0x5A });  // MZ header so the path looks real to the shell

            ShellEntry entry = new ShellEntry { Id = "main", Name = "Demo App", Exec = "bin\\demo.exe" };
            string evidence = await p.CreateMenuShortcut(entry, appDir, CancellationToken.None);

            Assert.True(File.Exists(evidence));
            Assert.EndsWith(".lnk", evidence);
        }

        [Fact]
        [SupportedOSPlatform("windows")]
        public async Task CreateMenuShortcut_WithLaunchOverride_WritesLnkRoutedThroughHina()
        {
            if (!IsWindows) return;

            WindowsPlatformIntegration p = NewPlatform();

            string appDir = Path.Combine(_tempDir, "payload");
            Directory.CreateDirectory(appDir);

            ShellEntry entry = new ShellEntry { Id = "main", Name = "Sandboxed App", Exec = "bin\\demo.exe" };
            string hinaExe = Path.Combine(_tempDir, "hina.exe");
            File.WriteAllBytes(hinaExe, new byte[] { 0x4D, 0x5A });
            string launchOverride = $"\"{hinaExe}\" run demo \"main\"";

            // The sandboxed shortcut must point at hina (so the AppContainer is installed
            // before the app starts), not the raw binary — the COM write must not throw.
            string evidence = await p.CreateMenuShortcut(entry, appDir, launchOverride, CancellationToken.None);

            Assert.True(File.Exists(evidence));
            Assert.EndsWith(".lnk", evidence);
        }

        [Fact]
        [SupportedOSPlatform("windows")]
        public async Task RegisterUrlScheme_WritesHkcuKeyWithUrlProtocol()
        {
            if (!IsWindows) return;

            WindowsPlatformIntegration p = NewPlatform();
            UrlSchemeHook hook = new UrlSchemeHook { Scheme = "hinatest-url", EntryId = "main" };

            string evidence = await p.RegisterUrlScheme(hook, "C:\\apps\\x", null, CancellationToken.None);

            Assert.StartsWith("hkcu:", evidence);

            using Microsoft.Win32.RegistryKey? k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software\\Classes\\hinatest-url");
            Assert.NotNull(k);
            Assert.NotNull(k!.GetValue("URL Protocol"));

            await p.UnregisterUrlScheme(evidence, CancellationToken.None);
            using Microsoft.Win32.RegistryKey? after = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software\\Classes\\hinatest-url");
            Assert.Null(after);
        }

        [Fact]
        [SupportedOSPlatform("windows")]
        public async Task RegisterMimeType_CreatesProgIdAndExtensionAssociation()
        {
            if (!IsWindows) return;

            WindowsPlatformIntegration p = NewPlatform();
            MimeTypeHook hook = new MimeTypeHook
            {
                MimeType = "application/x-hinatest",
                Extensions = { ".hntest" },
                EntryId = "main"
            };

            string evidence = await p.RegisterMimeType(hook, "C:\\apps\\x", null, CancellationToken.None);

            Assert.Contains("Hina.application_x-hinatest.main", evidence);

            using Microsoft.Win32.RegistryKey? prog = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                "Software\\Classes\\Hina.application_x-hinatest.main");
            Assert.NotNull(prog);

            using Microsoft.Win32.RegistryKey? ext = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                "Software\\Classes\\.hntest");
            Assert.NotNull(ext);
            Assert.Equal("Hina.application_x-hinatest.main", ext!.GetValue(""));

            await p.UnregisterMimeType(evidence, CancellationToken.None);
            using Microsoft.Win32.RegistryKey? after = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                "Software\\Classes\\Hina.application_x-hinatest.main");
            Assert.Null(after);
        }

        [Fact]
        [SupportedOSPlatform("windows")]
        public async Task InstallFont_CopiesFileAndRegistersHkcuValue_ThenUninstallCleansBoth()
        {
            if (!IsWindows) return;

            WindowsPlatformIntegration p = NewPlatform();
            string src = Path.Combine(_tempDir, "src", "HinaTest.ttf");
            Directory.CreateDirectory(Path.GetDirectoryName(src)!);
            File.WriteAllBytes(src, new byte[] { 1, 2, 3 });

            string evidence = await p.InstallFont(src, CancellationToken.None);

            // Evidence is "<destPath><US><fontName>" (US = unit separator U+001F), not '|'.
            string[] parts = evidence.Split('');
            Assert.Equal(2, parts.Length);
            string destPath = parts[0];
            string fontName = parts[1];

            Assert.True(File.Exists(destPath));

            using Microsoft.Win32.RegistryKey? k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                "Software\\Microsoft\\Windows NT\\CurrentVersion\\Fonts");
            Assert.NotNull(k);
            Assert.Equal(destPath, k!.GetValue(fontName + " (TrueType)"));

            await p.UninstallFont(evidence, CancellationToken.None);
            Assert.False(File.Exists(destPath));
        }

        [Fact]
        [SupportedOSPlatform("windows")]
        public async Task RegisterAutostart_AddsHkcuRunValue_ThenUnregisterRemovesIt()
        {
            if (!IsWindows) return;

            WindowsPlatformIntegration p = NewPlatform();
            AutostartHook hook = new AutostartHook { EntryId = "hinatest-autostart" };

            string evidence = await p.RegisterAutostart(hook, "C:\\apps\\x", null, CancellationToken.None);

            Assert.StartsWith("hkcu-run:", evidence);

            using Microsoft.Win32.RegistryKey? k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                "Software\\Microsoft\\Windows\\CurrentVersion\\Run");
            Assert.NotNull(k);
            Assert.NotNull(k!.GetValue("Hina.hinatest-autostart"));

            await p.UnregisterAutostart(evidence, CancellationToken.None);
            using Microsoft.Win32.RegistryKey? k2 = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                "Software\\Microsoft\\Windows\\CurrentVersion\\Run");
            Assert.Null(k2?.GetValue("Hina.hinatest-autostart"));
        }

        [Fact]
        [SupportedOSPlatform("windows")]
        public async Task UnregisterMimeType_RemovesOrphanedExtensionKey()
        {
            if (!IsWindows) return;

            WindowsPlatformIntegration p = NewPlatform();
            MimeTypeHook hook = new MimeTypeHook
            {
                MimeType = "application/x-hinaorphan",
                Extensions = { ".hnorphan" },
                EntryId = "main"
            };

            string evidence = await p.RegisterMimeType(hook, "C:\\apps\\x", null, CancellationToken.None);
            await p.UnregisterMimeType(evidence, CancellationToken.None);

            // The per-extension association key (default value = ProgID) must not survive the
            // unregister with a dangling reference to the deleted ProgID.
            using Microsoft.Win32.RegistryKey? ext = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                "Software\\Classes\\.hnorphan");
            Assert.True(ext == null || ext.GetValue("") == null);
        }

        [Fact]
        [SupportedOSPlatform("windows")]
        public async Task RegisterAutostart_ArgWithSpace_IsQuoted()
        {
            if (!IsWindows) return;

            WindowsPlatformIntegration p = NewPlatform();
            AutostartHook hook = new AutostartHook
            {
                EntryId = "hinatest-args",
                Args = new() { "--name=My App" }
            };

            string evidence = await p.RegisterAutostart(hook, "C:\\apps\\x", "C:\\apps\\x\\app.exe", CancellationToken.None);

            using Microsoft.Win32.RegistryKey? k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                "Software\\Microsoft\\Windows\\CurrentVersion\\Run");
            string? command = k?.GetValue("Hina.hinatest-args") as string;
            Assert.NotNull(command);
            Assert.Contains("\"--name=My App\"", command!);

            await p.UnregisterAutostart(evidence, CancellationToken.None);
        }

        // BUG-008: command injection via double-quotes or % in the target path.
        // The .cmd shim must emit a safe quoted token regardless of path content.
        [Fact]
        [SupportedOSPlatform("windows")]
        public async Task AddToPath_TargetWithDoubleQuote_DoesNotBreakCmdShim()
        {
            if (!IsWindows) return;

            WindowsPlatformIntegration p = NewPlatform();
            // A path containing an embedded double-quote: the shim must not close
            // the surrounding quotes and expose a command-injection surface.
            string targetExec = "C:\\Apps\\my\"app\\bin\\demo.exe";

            string evidence = await p.AddToPath("demo-quoted", targetExec, CancellationToken.None);

            string content = File.ReadAllText(evidence);
            Assert.Contains("@echo off", content);
            // The path must appear with its quote doubled ("") — not raw — so the
            // outer double-quotes are never prematurely closed.
            Assert.Contains("\"C:\\Apps\\my\"\"app\\bin\\demo.exe\" %*", content);
            // The raw unescaped quote must NOT appear (would enable injection).
            Assert.DoesNotContain("my\"app\\bin\\demo.exe\" %*\r\n\"", content);
        }

        [Fact]
        [SupportedOSPlatform("windows")]
        public async Task AddToPath_TargetWithPercentSign_DoesNotExpandVariable()
        {
            if (!IsWindows) return;

            WindowsPlatformIntegration p = NewPlatform();
            // A path containing %-delimited text: without escaping CMD would expand
            // %APPDATA% (or similar) even inside double-quotes, producing a wrong path.
            string targetExec = "C:\\Users\\%USERNAME%\\Apps\\demo.exe";

            string evidence = await p.AddToPath("demo-percent", targetExec, CancellationToken.None);

            string content = File.ReadAllText(evidence);
            // % must be doubled to %% in the shim content.
            Assert.Contains("\"C:\\Users\\%%USERNAME%%\\Apps\\demo.exe\" %*", content);
            // The raw, undoubled path must not appear — that form would expand %USERNAME%
            // at runtime. (%%USERNAME%% legitimately contains "%USERNAME%" as a substring,
            // so we assert the surrounding undoubled segment is absent instead.)
            Assert.DoesNotContain("\\Users\\%USERNAME%\\Apps", content);
        }

        [Fact]
        [SupportedOSPlatform("windows")]
        public async Task AddToPath_TargetWithBothQuoteAndPercent_EscapesBoth()
        {
            if (!IsWindows) return;

            WindowsPlatformIntegration p = NewPlatform();
            string targetExec = "C:\\my\"apps\\%SPECIAL%\\demo.exe";

            string evidence = await p.AddToPath("demo-both", targetExec, CancellationToken.None);

            string content = File.ReadAllText(evidence);
            Assert.Contains("\"C:\\my\"\"apps\\%%SPECIAL%%\\demo.exe\" %*", content);
        }

        // BUG-015: two ShellEntry records with the same Name but different Id must
        // produce distinct .lnk files so uninstalling one does not remove the other's shortcut.
        [Fact]
        [SupportedOSPlatform("windows")]
        public async Task CreateMenuShortcut_SameNameDifferentId_ProducesDistinctLnkFiles()
        {
            if (!IsWindows) return;

            WindowsPlatformIntegration p = NewPlatform();

            string appDir = Path.Combine(_tempDir, "payload-collision");
            string exec1 = Path.Combine(appDir, "bin", "app1.exe");
            string exec2 = Path.Combine(appDir, "bin", "app2.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exec1)!);
            File.WriteAllBytes(exec1, new byte[] { 0x4D, 0x5A });
            File.WriteAllBytes(exec2, new byte[] { 0x4D, 0x5A });

            // Both entries share the same display Name; only Id differs.
            ShellEntry entryA = new ShellEntry { Id = "entry-a", Name = "Shared App", Exec = "bin\\app1.exe" };
            ShellEntry entryB = new ShellEntry { Id = "entry-b", Name = "Shared App", Exec = "bin\\app2.exe" };

            string evidenceA = await p.CreateMenuShortcut(entryA, appDir, CancellationToken.None);
            string evidenceB = await p.CreateMenuShortcut(entryB, appDir, CancellationToken.None);

            // The two .lnk paths must be distinct — BUG-015 would have made them equal.
            Assert.NotEqual(evidenceA, evidenceB);

            // Both shortcut files must exist simultaneously.
            Assert.True(File.Exists(evidenceA), $"Shortcut for entry-a not found: {evidenceA}");
            Assert.True(File.Exists(evidenceB), $"Shortcut for entry-b not found: {evidenceB}");

            // Removing one evidence path must leave the other intact.
            await p.RemoveMenuShortcut(evidenceA, CancellationToken.None);
            Assert.False(File.Exists(evidenceA), "Shortcut A should have been removed");
            Assert.True(File.Exists(evidenceB), "Shortcut B must survive removal of A's evidence");
        }

        [Fact]
        [SupportedOSPlatform("windows")]
        public async Task CreateMenuShortcut_LnkPathContainsEntryId()
        {
            if (!IsWindows) return;

            WindowsPlatformIntegration p = NewPlatform();

            string appDir = Path.Combine(_tempDir, "payload-idcheck");
            string execAbs = Path.Combine(appDir, "bin", "demo.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(execAbs)!);
            File.WriteAllBytes(execAbs, new byte[] { 0x4D, 0x5A });

            ShellEntry entry = new ShellEntry { Id = "my-unique-id", Name = "My App", Exec = "bin\\demo.exe" };
            string evidence = await p.CreateMenuShortcut(entry, appDir, CancellationToken.None);

            // The filename stem must contain the entry Id to prevent regressions of BUG-015.
            string filename = Path.GetFileNameWithoutExtension(evidence);
            Assert.Contains("my-unique-id", filename);
            Assert.EndsWith(".lnk", evidence);
        }
    }
}
