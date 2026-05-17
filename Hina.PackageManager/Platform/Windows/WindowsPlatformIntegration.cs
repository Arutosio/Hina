using System;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Paths;
using Microsoft.Win32;
using Reg = Microsoft.Win32.Registry;

namespace Hina.PackageManager.Platform.Windows
{
    // Windows shell integration. All operations are user-scope (HKCU, %LOCALAPPDATA%,
    // per-user fonts) so the install never needs admin/UAC. The implementation is marked
    // [SupportedOSPlatform("windows")] so it compiles on every host but runtime calls
    // are gated by PlatformIntegrationFactory.
    [SupportedOSPlatform("windows")]
    public sealed class WindowsPlatformIntegration : IPlatformIntegration
    {
        private readonly string _userBinDir;
        private readonly string _startMenuDir;
        private readonly string _userFontsDir;

        public WindowsPlatformIntegration(InstallPaths paths)
            : this(paths.UserBinDir, DefaultStartMenuDir(), DefaultUserFontsDir())
        {
        }

        // Test seam.
        public WindowsPlatformIntegration(string userBinDir, string startMenuDir, string? userFontsDir = null)
        {
            _userBinDir = userBinDir;
            _startMenuDir = startMenuDir;
            _userFontsDir = userFontsDir ?? DefaultUserFontsDir();
        }

        public string OsId => "windows";
        public string UserBinDir => _userBinDir;
        public string UserAppsDir => _startMenuDir;

        // ---- Menu shortcut (.lnk via IShellLink) ----

        public Task<string> CreateMenuShortcut(ShellEntry entry, string appDir, CancellationToken ct)
        {
            Directory.CreateDirectory(_startMenuDir);

            string linkPath = Path.Combine(_startMenuDir, SanitizeFileName(entry.Name) + ".lnk");
            string targetPath = Path.Combine(appDir, entry.Exec);
            string workingDir = Path.GetDirectoryName(targetPath) ?? appDir;
            string? iconPath = entry.Icon != null ? Path.Combine(appDir, entry.Icon) : null;

            ShellLink.Create(linkPath, targetPath, workingDir, entry.Name, iconPath);
            return Task.FromResult(linkPath);
        }

        public Task RemoveMenuShortcut(string evidencePath, CancellationToken ct)
        {
            TryDeleteFile(evidencePath);
            return Task.CompletedTask;
        }

        // ---- AddToPath (.cmd wrapper in %LOCALAPPDATA%\Hina\bin) ----
        // Symlinks on Windows historically required admin/Developer Mode; the .cmd shim
        // avoids that entirely and behaves the same when launched from PATH.

        public Task<string> AddToPath(string name, string targetExec, CancellationToken ct)
        {
            Directory.CreateDirectory(_userBinDir);
            EnsureBinDirOnUserPath();

            string cmdPath = Path.Combine(_userBinDir, name + ".cmd");
            // %* forwards all arguments verbatim.
            string content = $"@echo off\r\n\"{targetExec}\" %*\r\n";
            File.WriteAllText(cmdPath, content);
            return Task.FromResult(cmdPath);
        }

        public Task RemoveFromPath(string evidencePath, CancellationToken ct)
        {
            TryDeleteFile(evidencePath);
            return Task.CompletedTask;
        }

        // ---- MIME type via HKCU\Software\Classes ----

        public Task<string> RegisterMimeType(MimeTypeHook hook, string appDir, CancellationToken ct)
        {
            // ProgID keyed off the mime-type identifier so re-registering for the same
            // (mime, entry) overwrites in place rather than accumulating duplicates.
            string progId = "Hina." + SanitizeRegId(hook.MimeType) + "." + SanitizeRegId(hook.EntryId);
            string evidence = $"hkcu:Software\\Classes\\{progId}";

            using (RegistryKey progKey = Reg.CurrentUser.CreateSubKey($"Software\\Classes\\{progId}"))
            {
                progKey.SetValue("", $"Hina MIME {hook.MimeType}");
                using RegistryKey shellOpen = progKey.CreateSubKey("shell\\open\\command");
                // Exec path resolved by caller — Hina drives MIME *registration*, not launch.
                // We keep a placeholder so the shell can still launch via this ProgID when
                // the upstream entry's exec is wired by the user.
                shellOpen.SetValue("", "\"%1\"");
            }

            foreach (string ext in hook.Extensions)
            {
                string extKey = ext.StartsWith(".") ? ext : ("." + ext);
                using RegistryKey k = Reg.CurrentUser.CreateSubKey($"Software\\Classes\\{extKey}");
                k.SetValue("", progId);
            }

            return Task.FromResult(evidence);
        }

        public Task UnregisterMimeType(string evidencePath, CancellationToken ct)
        {
            if (TryParseHkcuPath(evidencePath, out string? subKey))
            {
                try { Reg.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false); } catch { }
            }
            return Task.CompletedTask;
        }

        // ---- URL scheme via HKCU\Software\Classes\<scheme> ----

        public Task<string> RegisterUrlScheme(UrlSchemeHook hook, string appDir, CancellationToken ct)
        {
            string evidence = $"hkcu:Software\\Classes\\{hook.Scheme}";

            using RegistryKey k = Reg.CurrentUser.CreateSubKey($"Software\\Classes\\{hook.Scheme}");
            k.SetValue("", "URL:" + hook.Scheme);
            k.SetValue("URL Protocol", "");
            using RegistryKey cmd = k.CreateSubKey("shell\\open\\command");
            cmd.SetValue("", "\"%1\"");
            return Task.FromResult(evidence);
        }

        public Task UnregisterUrlScheme(string evidencePath, CancellationToken ct)
        {
            if (TryParseHkcuPath(evidencePath, out string? subKey))
            {
                try { Reg.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false); } catch { }
            }
            return Task.CompletedTask;
        }

        // ---- Font: per-user (no admin) install ----

        public Task<string> InstallFont(string fontFile, CancellationToken ct)
        {
            Directory.CreateDirectory(_userFontsDir);
            string destPath = Path.Combine(_userFontsDir, Path.GetFileName(fontFile));
            File.Copy(fontFile, destPath, overwrite: true);

            // Per-user Fonts registry key — surfaces the font to the user session without
            // running as admin. Value name = display name + " (TrueType)" by convention.
            string fontName = Path.GetFileNameWithoutExtension(fontFile);
            string regSubKey = "Software\\Microsoft\\Windows NT\\CurrentVersion\\Fonts";
            using (RegistryKey k = Reg.CurrentUser.CreateSubKey(regSubKey))
            {
                k.SetValue(fontName + " (TrueType)", destPath);
            }

            // Evidence carries both the file path and the registry value name so uninstall
            // can clean both. Pipe-separated to match the InstallFont evidence shape
            // used elsewhere in the codebase.
            return Task.FromResult(destPath + "|" + fontName);
        }

        public Task UninstallFont(string evidencePath, CancellationToken ct)
        {
            int pipe = evidencePath.IndexOf('|');
            string filePath = pipe > 0 ? evidencePath.Substring(0, pipe) : evidencePath;
            string? fontName = pipe > 0 ? evidencePath.Substring(pipe + 1) : null;

            TryDeleteFile(filePath);

            if (fontName != null)
            {
                try
                {
                    using RegistryKey? k = Reg.CurrentUser.OpenSubKey(
                        "Software\\Microsoft\\Windows NT\\CurrentVersion\\Fonts", writable: true);
                    k?.DeleteValue(fontName + " (TrueType)", throwOnMissingValue: false);
                }
                catch { }
            }
            return Task.CompletedTask;
        }

        // ---- Autostart: HKCU\Software\Microsoft\Windows\CurrentVersion\Run ----

        public Task<string> RegisterAutostart(AutostartHook hook, string appDir, CancellationToken ct)
        {
            string valueName = "Hina." + SanitizeRegId(hook.EntryId);
            string evidence = "hkcu-run:" + valueName;

            using RegistryKey k = Reg.CurrentUser.CreateSubKey(
                "Software\\Microsoft\\Windows\\CurrentVersion\\Run")!;
            // Caller's appDir + entry exec isn't known here, but the run-on-login behaviour
            // requires a real command. We stash a placeholder pointing at the registered ProgID;
            // higher-level callers wiring exec into autostart should supply a target via a
            // future schema field — for now we record the value-name so uninstall can clean.
            k.SetValue(valueName, "");
            return Task.FromResult(evidence);
        }

        public Task UnregisterAutostart(string evidencePath, CancellationToken ct)
        {
            const string prefix = "hkcu-run:";
            if (!evidencePath.StartsWith(prefix)) return Task.CompletedTask;
            string valueName = evidencePath.Substring(prefix.Length);

            try
            {
                using RegistryKey? k = Reg.CurrentUser.OpenSubKey(
                    "Software\\Microsoft\\Windows\\CurrentVersion\\Run", writable: true);
                k?.DeleteValue(valueName, throwOnMissingValue: false);
            }
            catch { }
            return Task.CompletedTask;
        }

        // ---- Helpers ----

        private void EnsureBinDirOnUserPath()
        {
            string? current = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
            if (current == null)
            {
                Environment.SetEnvironmentVariable("PATH", _userBinDir, EnvironmentVariableTarget.User);
                return;
            }
            foreach (string segment in current.Split(';'))
            {
                if (string.Equals(segment.TrimEnd('\\'), _userBinDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            Environment.SetEnvironmentVariable("PATH", current + ";" + _userBinDir, EnvironmentVariableTarget.User);
        }

        private static bool TryParseHkcuPath(string evidence, out string subKey)
        {
            const string prefix = "hkcu:";
            if (evidence.StartsWith(prefix))
            {
                subKey = evidence.Substring(prefix.Length);
                return true;
            }
            subKey = string.Empty;
            return false;
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        private static string SanitizeFileName(string value)
        {
            StringBuilder sb = new StringBuilder(value.Length);
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in value)
            {
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }
            string s = sb.ToString().Trim();
            return s.Length == 0 ? "Hina" : s;
        }

        private static string SanitizeRegId(string value)
        {
            StringBuilder sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
                else sb.Append('_');
            }
            return sb.Length == 0 ? "x" : sb.ToString();
        }

        private static string DefaultStartMenuDir()
        {
            string startMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            return Path.Combine(startMenu, "Hina");
        }

        private static string DefaultUserFontsDir()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "Microsoft", "Windows", "Fonts");
        }
    }
}
