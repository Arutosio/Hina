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
using static Hina.PackageManager.Platform.PlatformText;

namespace Hina.PackageManager.Platform.MacOS
{
    // macOS shell integration. User-scope only: ~/Applications, ~/Library/Fonts,
    // ~/Library/LaunchAgents. No admin / no sudo. No shell-outs — pure file writes
    // so the impl is AOT-clean and testable on every host. Launch Services picks up
    // the new .app bundles on its next scan (Finder visit / login). Users wanting
    // immediate registration can run `/usr/bin/lsregister -f <path>` manually.
    [SupportedOSPlatform("macos")]
    public sealed class MacOSPlatformIntegration : IPlatformIntegration
    {
        private readonly string _userBinDir;
        private readonly string _userAppsDir;
        private readonly string _userFontsDir;
        private readonly string _launchAgentsDir;
        private readonly ILogger _logger;

        public MacOSPlatformIntegration(InstallPaths paths, ILogger? logger = null)
            : this(paths.UserBinDir, DefaultUserAppsDir(), DefaultUserFontsDir(), DefaultLaunchAgentsDir(), logger)
        {
        }

        // Test seam.
        public MacOSPlatformIntegration(string userBinDir, string userAppsDir, string? userFontsDir = null, string? launchAgentsDir = null, ILogger? logger = null)
        {
            _userBinDir = userBinDir;
            _userAppsDir = userAppsDir;
            _userFontsDir = userFontsDir ?? DefaultUserFontsDir();
            _launchAgentsDir = launchAgentsDir ?? DefaultLaunchAgentsDir();
            _logger = logger ?? NullLogger.Instance;
        }

        public string OsId => "macos";
        public string UserBinDir => _userBinDir;
        public string UserAppsDir => _userAppsDir;

        // ---- Menu shortcut: minimal .app bundle ----

        public Task<string> CreateMenuShortcut(ShellEntry entry, string appDir, CancellationToken ct)
            => CreateMenuShortcut(entry, appDir, launchOverride: null, ct);

        // A sandboxed app routes through `hina run` (launchOverride) so the Seatbelt
        // sandbox is installed before the app process starts. The bundle exec then
        // becomes a tiny script that execs the override command — symlinking straight
        // to the binary would bypass the sandbox.
        public Task<string> CreateMenuShortcut(ShellEntry entry, string appDir, string? launchOverride, CancellationToken ct)
        {
            string bundlePath = WriteAppBundle(
                bundleName: entry.Name,
                bundleId: "com.hina." + SanitizeId(entry.Id),
                execTarget: Path.Combine(appDir, entry.Exec),
                iconRelative: entry.Icon != null ? Path.Combine(appDir, entry.Icon) : null,
                documentTypes: null,
                urlTypes: null,
                launchOverride: launchOverride);
            return Task.FromResult(bundlePath);
        }

        public Task RemoveMenuShortcut(string evidencePath, CancellationToken ct)
        {
            TryDeleteBundle(evidencePath);
            return Task.CompletedTask;
        }

        // ---- AddToPath (symlink in ~/.local/bin) ----

        public Task<string> AddToPath(string name, string targetExec, CancellationToken ct)
        {
            Directory.CreateDirectory(_userBinDir);

            string linkPath = Path.Combine(_userBinDir, name);
            if (File.Exists(linkPath) || new FileInfo(linkPath).LinkTarget != null)
            {
                TryDeleteFile(linkPath, _logger);
            }
            File.CreateSymbolicLink(linkPath, targetExec);
            return Task.FromResult(linkPath);
        }

        public Task RemoveFromPath(string evidencePath, CancellationToken ct)
        {
            TryDeleteFile(evidencePath, _logger);
            return Task.CompletedTask;
        }

        // ---- MIME type: helper .app bundle with CFBundleDocumentTypes ----

        public Task<string> RegisterMimeType(MimeTypeHook hook, string appDir, string? entryExecAbs, CancellationToken ct)
        {
            string bundlePath = WriteAppBundle(
                bundleName: $"Hina MIME {hook.MimeType}",
                bundleId: "com.hina.mime." + SanitizeId(hook.MimeType) + "." + SanitizeId(hook.EntryId),
                execTarget: entryExecAbs,
                iconRelative: null,
                documentTypes: BuildDocumentTypesPlist(hook),
                urlTypes: null);
            return Task.FromResult(bundlePath);
        }

        public Task UnregisterMimeType(string evidencePath, CancellationToken ct)
        {
            TryDeleteBundle(evidencePath);
            return Task.CompletedTask;
        }

        // ---- URL scheme: helper .app bundle with CFBundleURLTypes ----

        public Task<string> RegisterUrlScheme(UrlSchemeHook hook, string appDir, string? entryExecAbs, CancellationToken ct)
        {
            string bundlePath = WriteAppBundle(
                bundleName: $"Hina URL {hook.Scheme}",
                bundleId: "com.hina.url." + SanitizeId(hook.Scheme) + "." + SanitizeId(hook.EntryId),
                execTarget: entryExecAbs,
                iconRelative: null,
                documentTypes: null,
                urlTypes: BuildUrlTypesPlist(hook));
            return Task.FromResult(bundlePath);
        }

        public Task UnregisterUrlScheme(string evidencePath, CancellationToken ct)
        {
            TryDeleteBundle(evidencePath);
            return Task.CompletedTask;
        }

        // ---- Font: copy to ~/Library/Fonts ----

        public Task<string> InstallFont(string fontFile, CancellationToken ct)
        {
            // Legacy overload — new code paths use the (file, appName, ct) overload.
            Directory.CreateDirectory(_userFontsDir);
            string destPath = Path.Combine(_userFontsDir, Path.GetFileName(fontFile));
            File.Copy(fontFile, destPath, overwrite: true);
            return Task.FromResult(destPath);
        }

        public Task<string> InstallFont(string fontFile, string appName, CancellationToken ct)
        {
            Directory.CreateDirectory(_userFontsDir);
            string destPath = Path.Combine(_userFontsDir, $"hina-{SanitizeId(appName)}-{Path.GetFileName(fontFile)}");
            File.Copy(fontFile, destPath, overwrite: true);
            return Task.FromResult(destPath);
        }

        public Task UninstallFont(string evidencePath, CancellationToken ct)
        {
            TryDeleteFile(evidencePath, _logger);
            return Task.CompletedTask;
        }

        public bool IsEvidenceDangling(string action, string evidence)
        {
            switch (action)
            {
                case "addToPath":
                    if (!File.Exists(evidence) && new FileInfo(evidence).LinkTarget == null) return true;
                    string? target = new FileInfo(evidence).LinkTarget;
                    return target != null && !File.Exists(target);

                case "installFont":
                    foreach (string p in evidence.Split('|', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!File.Exists(p)) return true;
                    }
                    return false;

                case "shellEntry":
                    // macOS shell entries are .app bundles (directories).
                    return !Directory.Exists(evidence);

                case "registerMimeType":
                case "registerUrlScheme":
                    // Helper .app bundle.
                    return !Directory.Exists(evidence);

                case "registerAutostart":
                    return !File.Exists(evidence);

                default:
                    return !File.Exists(evidence) && !Directory.Exists(evidence);
            }
        }

        // ---- Autostart: ~/Library/LaunchAgents/*.plist ----

        public Task<string> RegisterAutostart(AutostartHook hook, string appDir, string? entryExecAbs, CancellationToken ct)
        {
            Directory.CreateDirectory(_launchAgentsDir);

            string label = "com.hina.autostart." + SanitizeId(hook.EntryId);
            string plistPath = Path.Combine(_launchAgentsDir, label + ".plist");

            // ProgramArguments[0] must be the executable path so launchd can start it. Fall back to
            // the EntryId only if it couldn't be resolved (shouldn't happen for a valid descriptor).
            string program = string.IsNullOrEmpty(entryExecAbs) ? hook.EntryId : entryExecAbs;

            StringBuilder args = new StringBuilder();
            if (hook.Args != null)
            {
                foreach (string a in hook.Args)
                {
                    args.Append("        <string>").Append(EscapeXml(a)).Append("</string>\n");
                }
            }

            string plist =
$@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
    <key>Label</key>
    <string>{EscapeXml(label)}</string>
    <key>ProgramArguments</key>
    <array>
        <string>{EscapeXml(program)}</string>
{args}    </array>
    <key>RunAtLoad</key>
    <true/>
</dict>
</plist>
";
            File.WriteAllText(plistPath, plist);
            return Task.FromResult(plistPath);
        }

        public Task UnregisterAutostart(string evidencePath, CancellationToken ct)
        {
            TryDeleteFile(evidencePath, _logger);
            return Task.CompletedTask;
        }

        // ---- App bundle writer ----

        private string WriteAppBundle(string bundleName, string bundleId, string? execTarget, string? iconRelative, string? documentTypes, string? urlTypes, string? launchOverride = null)
        {
            Directory.CreateDirectory(_userAppsDir);

            string bundlePath = Path.Combine(_userAppsDir, SanitizeFileName(bundleName) + ".app");
            string contentsDir = Path.Combine(bundlePath, "Contents");
            string macosDir = Path.Combine(contentsDir, "MacOS");
            Directory.CreateDirectory(macosDir);

            // Stub or symlinked executable so Launch Services treats the bundle as launchable.
            string execName = SanitizeFileName(bundleName);
            string execPath = Path.Combine(macosDir, execName);
            if (launchOverride != null)
            {
                // Sandboxed app: the bundle exec is a script that routes through
                // `hina run` so the sandbox is applied before the real binary starts.
                if (File.Exists(execPath) || new FileInfo(execPath).LinkTarget != null)
                {
                    TryDeleteFile(execPath, _logger);
                }
                File.WriteAllText(execPath, "#!/bin/sh\nexec " + StripControl(launchOverride) + " \"$@\"\n");
                TrySetExecutable(execPath);
            }
            else if (execTarget != null)
            {
                if (File.Exists(execPath) || new FileInfo(execPath).LinkTarget != null)
                {
                    TryDeleteFile(execPath, _logger);
                }
                File.CreateSymbolicLink(execPath, execTarget);
            }
            else
            {
                // Helper bundles (MIME/URL) don't launch anything; tiny no-op stub keeps Launch
                // Services happy by giving the bundle a valid CFBundleExecutable target.
                File.WriteAllText(execPath, "#!/bin/sh\nexit 0\n");
                TrySetExecutable(execPath);
            }

            string plistPath = Path.Combine(contentsDir, "Info.plist");
            string plist = BuildInfoPlist(bundleName, bundleId, execName, documentTypes, urlTypes);
            File.WriteAllText(plistPath, plist);

            return bundlePath;
        }

        private void TrySetExecutable(string execPath)
        {
            try
            {
                // 0755 — owner rwx, group/others rx. Best-effort: ignore on platforms that don't support it.
                File.SetUnixFileMode(execPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Best-effort: could not set exec mode on {Path}", execPath); }
        }

        // Drop control characters (newlines especially) so a launch override can't
        // inject extra script lines into the bundle exec.
        private static string StripControl(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (!char.IsControl(c)) sb.Append(c);
            }
            return sb.ToString();
        }

        private static string BuildInfoPlist(string bundleName, string bundleId, string execName, string? documentTypes, string? urlTypes)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
            sb.Append("<plist version=\"1.0\">\n<dict>\n");
            sb.Append("    <key>CFBundleName</key>\n    <string>").Append(EscapeXml(bundleName)).Append("</string>\n");
            sb.Append("    <key>CFBundleIdentifier</key>\n    <string>").Append(EscapeXml(bundleId)).Append("</string>\n");
            sb.Append("    <key>CFBundleExecutable</key>\n    <string>").Append(EscapeXml(execName)).Append("</string>\n");
            sb.Append("    <key>CFBundlePackageType</key>\n    <string>APPL</string>\n");
            sb.Append("    <key>CFBundleShortVersionString</key>\n    <string>1.0</string>\n");
            sb.Append("    <key>X-Hina-Managed</key>\n    <true/>\n");

            if (documentTypes != null) sb.Append(documentTypes);
            if (urlTypes != null) sb.Append(urlTypes);

            sb.Append("</dict>\n</plist>\n");
            return sb.ToString();
        }

        private static string BuildDocumentTypesPlist(MimeTypeHook hook)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("    <key>CFBundleDocumentTypes</key>\n    <array>\n");
            sb.Append("        <dict>\n");
            sb.Append("            <key>CFBundleTypeName</key>\n            <string>").Append(EscapeXml(hook.MimeType)).Append("</string>\n");
            sb.Append("            <key>LSItemContentTypes</key>\n            <array>\n");
            sb.Append("                <string>").Append(EscapeXml(hook.MimeType)).Append("</string>\n");
            sb.Append("            </array>\n");
            sb.Append("            <key>CFBundleTypeExtensions</key>\n            <array>\n");
            foreach (string ext in hook.Extensions)
            {
                string trimmed = ext.StartsWith(".") ? ext.Substring(1) : ext;
                sb.Append("                <string>").Append(EscapeXml(trimmed)).Append("</string>\n");
            }
            sb.Append("            </array>\n");
            sb.Append("            <key>CFBundleTypeRole</key>\n            <string>Viewer</string>\n");
            sb.Append("        </dict>\n");
            sb.Append("    </array>\n");
            return sb.ToString();
        }

        private static string BuildUrlTypesPlist(UrlSchemeHook hook)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("    <key>CFBundleURLTypes</key>\n    <array>\n");
            sb.Append("        <dict>\n");
            sb.Append("            <key>CFBundleURLName</key>\n            <string>").Append(EscapeXml(hook.Scheme)).Append("</string>\n");
            sb.Append("            <key>CFBundleURLSchemes</key>\n            <array>\n");
            sb.Append("                <string>").Append(EscapeXml(hook.Scheme)).Append("</string>\n");
            sb.Append("            </array>\n");
            sb.Append("        </dict>\n");
            sb.Append("    </array>\n");
            return sb.ToString();
        }

        // ---- Helpers ----

        private void TryDeleteBundle(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (Exception ex)
            {
                // Fail-soft: uninstall must converge to clean even if a bundle is locked/gone.
                _logger.LogDebug(ex, "Fail-soft: could not delete bundle {Path}", path);
            }
        }

        private static string EscapeXml(string value)
        {
            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        private static string DefaultUserAppsDir()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Applications");
        }

        private static string DefaultUserFontsDir()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Fonts");
        }

        private static string DefaultLaunchAgentsDir()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "LaunchAgents");
        }
    }
}
