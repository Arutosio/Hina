using System;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Paths;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Reg = Microsoft.Win32.Registry;
using static Hina.PackageManager.Platform.PlatformText;

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
        private readonly ILogger _logger;

        public WindowsPlatformIntegration(InstallPaths paths, ILogger? logger = null)
            : this(paths.UserBinDir, DefaultStartMenuDir(), DefaultUserFontsDir(), logger)
        {
        }

        // Test seam.
        public WindowsPlatformIntegration(string userBinDir, string startMenuDir, string? userFontsDir = null, ILogger? logger = null)
        {
            _userBinDir = userBinDir;
            _startMenuDir = startMenuDir;
            _userFontsDir = userFontsDir ?? DefaultUserFontsDir();
            _logger = logger ?? NullLogger.Instance;
        }

        public string OsId => "windows";
        public string UserBinDir => _userBinDir;
        public string UserAppsDir => _startMenuDir;

        // ---- Menu shortcut (.lnk via IShellLink) ----

        public Task<string> CreateMenuShortcut(ShellEntry entry, string appDir, CancellationToken ct)
            => CreateMenuShortcut(entry, appDir, launchOverride: null, ct);

        // Sandbox-aware overload: a sandboxed app routes through `hina run` (launchOverride)
        // so the AppContainer is installed before the real binary starts. The .lnk then points
        // at the hina executable with `run <app> <entry>` as its arguments — pointing straight
        // at the binary would bypass the sandbox. Control chars are stripped (the override flows
        // into a persisted shortcut). Mirrors Linux/macOS.
        public Task<string> CreateMenuShortcut(ShellEntry entry, string appDir, string? launchOverride, CancellationToken ct)
        {
            Directory.CreateDirectory(_startMenuDir);

            // BUG-015: key the .lnk filename on both Name (for readability) and Id (for
            // uniqueness). entry.Id is validated as unique by DescriptorValidator; entry.Name is
            // not, so two entries sharing the same Name would otherwise collide and clobber each
            // other's shortcut. Option B from the bug report: "<Name>-<Id>.lnk" preserves the
            // human-readable Start-menu label (stem = display name) while the Id suffix makes
            // the file path unique. Mirrors the Linux per-Id desktop file naming convention.
            string linkPath = Path.Combine(_startMenuDir, SanitizeFileName(entry.Name) + "-" + SanitizeId(entry.Id) + ".lnk");
            string? iconPath = entry.Icon != null ? Path.Combine(appDir, entry.Icon) : null;

            string targetPath;
            string workingDir;
            string? arguments = null;
            if (launchOverride != null)
            {
                WindowsLaunchOverride parsed = WindowsLaunchOverride.Parse(StripControl(launchOverride));
                targetPath = parsed.Exe;
                arguments = parsed.Arguments;
                workingDir = appDir;
            }
            else
            {
                targetPath = Path.Combine(appDir, entry.Exec);
                workingDir = Path.GetDirectoryName(targetPath) ?? appDir;
            }

            ShellLink.Create(linkPath, targetPath, workingDir, entry.Name, iconPath, arguments);
            return Task.FromResult(linkPath);
        }

        public Task RemoveMenuShortcut(string evidencePath, CancellationToken ct)
        {
            TryDeleteFile(evidencePath, _logger);
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
            // EscapeCmdPath ensures the path is safe inside a batch double-quoted token:
            //   " → "" (batch quote-doubling; does NOT close the surrounding quotes)
            //   % → %% (prevents variable expansion even inside double-quotes)
            // No other metachar (^, &, |, <, >) needs escaping when the value is
            // already enclosed in double-quotes; they are treated as literals there.
            string content = $"@echo off\r\n\"{EscapeCmdPath(targetExec)}\" %*\r\n";
            File.WriteAllText(cmdPath, content);
            return Task.FromResult(cmdPath);
        }

        // Escape a filesystem path for embedding inside a CMD double-quoted token.
        // Batch double-quoting rules: " is represented as "" (not \"); % must be
        // doubled to prevent variable expansion which happens even inside quotes.
        private static string EscapeCmdPath(string path)
        {
            // Fast-path: no characters that need escaping.
            if (path.IndexOf('"') < 0 && path.IndexOf('%') < 0)
                return path;

            StringBuilder sb = new StringBuilder(path.Length + 4);
            foreach (char c in path)
            {
                if (c == '"') { sb.Append('"'); sb.Append('"'); }
                else if (c == '%') { sb.Append('%'); sb.Append('%'); }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public Task RemoveFromPath(string evidencePath, CancellationToken ct)
        {
            TryDeleteFile(evidencePath, _logger);
            return Task.CompletedTask;
        }

        // ---- MIME type via HKCU\Software\Classes ----

        public Task<string> RegisterMimeType(MimeTypeHook hook, string appDir, string? entryExecAbs, CancellationToken ct)
        {
            // ProgID keyed off the mime-type identifier so re-registering for the same
            // (mime, entry) overwrites in place rather than accumulating duplicates.
            string progId = "Hina." + SanitizeRegId(hook.MimeType) + "." + SanitizeRegId(hook.EntryId);
            string evidence = $"hkcu:Software\\Classes\\{progId}";

            using (RegistryKey progKey = Reg.CurrentUser.CreateSubKey($"Software\\Classes\\{progId}"))
            {
                progKey.SetValue("", $"Hina MIME {hook.MimeType}");
                using RegistryKey shellOpen = progKey.CreateSubKey("shell\\open\\command");
                shellOpen.SetValue("", LaunchCommand(entryExecAbs));
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
                // Remove the per-extension association keys we pointed at this ProgID BEFORE
                // deleting the ProgID itself. RegisterMimeType writes a `Software\Classes\.ext`
                // key (default value = ProgID) per extension but the evidence only records the
                // ProgID subkey, so without this the `.ext` keys are orphaned with a default
                // value referencing a now-deleted ProgID — breaking "Open With" for that
                // extension and shadowing other handlers.
                string progId = subKey.Substring(subKey.LastIndexOf('\\') + 1);
                TryCleanupExtensionKeys(progId);

                try { Reg.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false); }
                catch (Exception ex) { _logger.LogDebug(ex, "Fail-soft: could not delete registry subkey {SubKey}", subKey); }
            }
            return Task.CompletedTask;
        }

        // Scan Software\Classes for `.ext` keys whose default value still equals progId and
        // drop that association. Only the default value we wrote is cleared (not unrelated
        // subkeys/values another app may own); a key left empty afterwards is removed.
        private void TryCleanupExtensionKeys(string progId)
        {
            try
            {
                using RegistryKey? classes = Reg.CurrentUser.OpenSubKey("Software\\Classes", writable: true);
                if (classes == null) return;

                foreach (string name in classes.GetSubKeyNames())
                {
                    if (!name.StartsWith(".", StringComparison.Ordinal)) continue;

                    bool nowEmpty = false;
                    try
                    {
                        using RegistryKey? extKey = classes.OpenSubKey(name, writable: true);
                        if (extKey == null) continue;
                        if (extKey.GetValue("") is not string def || !string.Equals(def, progId, StringComparison.Ordinal))
                        {
                            continue;
                        }
                        extKey.DeleteValue("", throwOnMissingValue: false);
                        nowEmpty = extKey.ValueCount == 0 && extKey.SubKeyCount == 0;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Fail-soft: could not clean extension key {Ext} for {ProgId}", name, progId);
                        continue;
                    }

                    if (nowEmpty)
                    {
                        try { classes.DeleteSubKey(name, throwOnMissingSubKey: false); }
                        catch (Exception ex) { _logger.LogDebug(ex, "Fail-soft: could not delete empty extension key {Ext}", name); }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Fail-soft: could not enumerate Software\\Classes to clean {ProgId}", progId);
            }
        }

        // ---- URL scheme via HKCU\Software\Classes\<scheme> ----

        public Task<string> RegisterUrlScheme(UrlSchemeHook hook, string appDir, string? entryExecAbs, CancellationToken ct)
        {
            string evidence = $"hkcu:Software\\Classes\\{hook.Scheme}";

            using RegistryKey k = Reg.CurrentUser.CreateSubKey($"Software\\Classes\\{hook.Scheme}");
            k.SetValue("", "URL:" + hook.Scheme);
            k.SetValue("URL Protocol", "");
            using RegistryKey cmd = k.CreateSubKey("shell\\open\\command");
            cmd.SetValue("", LaunchCommand(entryExecAbs));
            return Task.FromResult(evidence);
        }

        // Shell open command: "<exec>" "%1" when the entry exec is known, else just "%1" (the old
        // placeholder) so the key still exists for the user to wire up.
        private static string LaunchCommand(string? entryExecAbs) =>
            string.IsNullOrEmpty(entryExecAbs) ? "\"%1\"" : $"\"{entryExecAbs}\" \"%1\"";

        public Task UnregisterUrlScheme(string evidencePath, CancellationToken ct)
        {
            if (TryParseHkcuPath(evidencePath, out string? subKey))
            {
                try { Reg.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false); }
                catch (Exception ex) { _logger.LogDebug(ex, "Fail-soft: could not delete registry subkey {SubKey}", subKey); }
            }
            return Task.CompletedTask;
        }

        // ---- Font: per-user (no admin) install ----

        public Task<string> InstallFont(string fontFile, CancellationToken ct)
        {
            // Legacy overload — new code paths use the (file, appName, ct) overload.
            return InstallFontInternal(fontFile, appPrefix: null);
        }

        public Task<string> InstallFont(string fontFile, string appName, CancellationToken ct)
        {
            return InstallFontInternal(fontFile, appPrefix: SanitizeRegId(appName));
        }

        // ASCII unit-separator. Used in font evidence so a pipe in the filename
        // (legal on some Windows FS) doesn't corrupt the split. Back-compat with
        // pre-existing "|"-separated evidence is preserved in UninstallFont.
        private const char EvidenceSeparator = '';

        private Task<string> InstallFontInternal(string fontFile, string? appPrefix)
        {
            Directory.CreateDirectory(_userFontsDir);

            string filename = appPrefix == null
                ? Path.GetFileName(fontFile)
                : $"hina-{appPrefix}-{Path.GetFileName(fontFile)}";
            string destPath = Path.Combine(_userFontsDir, filename);
            File.Copy(fontFile, destPath, overwrite: true);

            // Per-user Fonts registry key — surfaces the font to the user session without
            // running as admin. Value name = display name + " (TrueType)" by convention.
            string fontName = Path.GetFileNameWithoutExtension(filename);
            string regSubKey = "Software\\Microsoft\\Windows NT\\CurrentVersion\\Fonts";
            using (RegistryKey k = Reg.CurrentUser.CreateSubKey(regSubKey))
            {
                k.SetValue(fontName + " (TrueType)", destPath);
            }

            return Task.FromResult(destPath + EvidenceSeparator + fontName);
        }

        public Task UninstallFont(string evidencePath, CancellationToken ct)
        {
            // Accept both the new US-separated shape and the legacy pipe-separated one.
            char sep = evidencePath.Contains(EvidenceSeparator) ? EvidenceSeparator
                     : evidencePath.Contains('|') ? '|'
                     : (char)0;

            string filePath;
            string? fontName;
            if (sep == 0)
            {
                filePath = evidencePath;
                fontName = null;
            }
            else
            {
                int idx = evidencePath.IndexOf(sep);
                filePath = evidencePath.Substring(0, idx);
                fontName = evidencePath.Substring(idx + 1);
            }

            TryDeleteFile(filePath, _logger);

            if (fontName != null)
            {
                try
                {
                    using RegistryKey? k = Reg.CurrentUser.OpenSubKey(
                        "Software\\Microsoft\\Windows NT\\CurrentVersion\\Fonts", writable: true);
                    k?.DeleteValue(fontName + " (TrueType)", throwOnMissingValue: false);
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Fail-soft: could not remove font registry value {Font}", fontName); }
            }
            return Task.CompletedTask;
        }

        public bool IsEvidenceDangling(string action, string evidence)
        {
            switch (action)
            {
                case "addToPath":
                    // .cmd shim file
                    return !File.Exists(evidence);

                case "installFont":
                    // A font hook with multiple files records one "<filePath><US><fontName>"
                    // segment per file, joined by '|' (see HookExecutor). Check EVERY file —
                    // the hook is dangling if any installed font is gone (matches Linux/macOS).
                    foreach (string segment in evidence.Split('|', StringSplitOptions.RemoveEmptyEntries))
                    {
                        int si = segment.IndexOf(EvidenceSeparator);
                        string filePart = si >= 0 ? segment.Substring(0, si) : segment;
                        if (!File.Exists(filePart)) return true;
                    }
                    return false;

                case "registerMimeType":
                case "registerUrlScheme":
                    // Evidence shape: "hkcu:<subkey>". Probe the subkey.
                    return IsHkcuSubkeyMissing(evidence, "hkcu:");

                case "registerAutostart":
                    // Evidence shape: "hkcu-run:<valueName>". Probe the value.
                    if (!evidence.StartsWith("hkcu-run:")) return false;
                    string valueName = evidence.Substring("hkcu-run:".Length);
                    try
                    {
                        using RegistryKey? k = Reg.CurrentUser.OpenSubKey(
                            "Software\\Microsoft\\Windows\\CurrentVersion\\Run");
                        return k?.GetValue(valueName) == null;
                    }
                    catch (Exception ex)
                    {
                        // Can't probe → treat as dangling so `verify --repair` reconciles it.
                        _logger.LogDebug(ex, "Could not probe autostart value {Value}; treating as dangling", valueName);
                        return true;
                    }

                case "shellEntry":
                    // .lnk file
                    return !File.Exists(evidence);

                default:
                    return !File.Exists(evidence);
            }
        }

        private bool IsHkcuSubkeyMissing(string evidence, string prefix)
        {
            if (!evidence.StartsWith(prefix)) return false;
            string subKey = evidence.Substring(prefix.Length);
            try
            {
                using RegistryKey? k = Reg.CurrentUser.OpenSubKey(subKey);
                return k == null;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not probe registry subkey {SubKey}; treating as dangling", subKey);
                return true;
            }
        }

        // ---- Autostart: HKCU\Software\Microsoft\Windows\CurrentVersion\Run ----

        public Task<string> RegisterAutostart(AutostartHook hook, string appDir, string? entryExecAbs, CancellationToken ct)
        {
            string valueName = "Hina." + SanitizeRegId(hook.EntryId);
            string evidence = "hkcu-run:" + valueName;

            using RegistryKey k = Reg.CurrentUser.CreateSubKey(
                "Software\\Microsoft\\Windows\\CurrentVersion\\Run")!;
            // Run-on-login needs the real executable command. Append any hook args; fall back to an
            // empty value (the old placeholder) only when the entry exec couldn't be resolved.
            string command = "";
            if (!string.IsNullOrEmpty(entryExecAbs))
            {
                command = $"\"{entryExecAbs}\"";
                if (hook.Args is { Count: > 0 })
                {
                    // Quote each arg individually: joining raw would let an arg containing a
                    // space (e.g. --name=My App) split into two tokens at launch.
                    foreach (string arg in hook.Args)
                    {
                        command += " " + QuoteWinArg(arg);
                    }
                }
            }
            k.SetValue(valueName, command);
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
            catch (Exception ex) { _logger.LogDebug(ex, "Fail-soft: could not remove autostart value {Value}", valueName); }
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

        // Quote a command-line argument for the Run value when it contains whitespace or a
        // quote, escaping embedded quotes the way CommandLineToArgvW expects.
        private static string QuoteWinArg(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            {
                return arg;
            }
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
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
