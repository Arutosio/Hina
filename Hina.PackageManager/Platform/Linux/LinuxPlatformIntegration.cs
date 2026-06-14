using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Paths;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static Hina.PackageManager.Platform.PlatformText;

namespace Hina.PackageManager.Platform.Linux
{
    // Linux shell integration. Writes XDG-compatible .desktop files and copies fonts into
    // the user-scope fonts directory. Does NOT shell out to update-desktop-database /
    // fc-cache: those caches refresh on next user session and shell-outs introduce flaky
    // dependencies into tests and AOT binaries. Users wanting an immediate refresh can run
    // those tools themselves.
    public sealed class LinuxPlatformIntegration : IPlatformIntegration
    {
        private readonly string _userBinDir;
        private readonly string _userAppsDir;
        private readonly string _userFontsDir;
        private readonly string _userAutostartDir;
        private readonly ILogger _logger;

        public LinuxPlatformIntegration(InstallPaths paths, ILogger? logger = null)
            : this(paths.UserBinDir, DefaultUserAppsDir(), DefaultUserFontsDir(), DefaultUserAutostartDir(), logger)
        {
        }

        // Test seam: caller controls every directory we touch.
        public LinuxPlatformIntegration(string userBinDir, string userAppsDir, string? userFontsDir = null, string? userAutostartDir = null, ILogger? logger = null)
        {
            _userBinDir = userBinDir;
            _userAppsDir = userAppsDir;
            _userFontsDir = userFontsDir ?? DefaultUserFontsDir();
            _userAutostartDir = userAutostartDir ?? DefaultUserAutostartDir();
            _logger = logger ?? NullLogger.Instance;
        }

        public string OsId => "linux";
        public string UserBinDir => _userBinDir;
        public string UserAppsDir => _userAppsDir;

        // ---- Menu shortcut ----

        public Task<string> CreateMenuShortcut(ShellEntry entry, string appDir, CancellationToken ct)
            => CreateMenuShortcut(entry, appDir, launchOverride: null, ct);

        public Task<string> CreateMenuShortcut(ShellEntry entry, string appDir, string? launchOverride, CancellationToken ct)
        {
            Directory.CreateDirectory(_userAppsDir);

            string fileName = $"hina-{SanitizeId(entry.Id)}.desktop";
            string targetPath = Path.Combine(_userAppsDir, fileName);

            string execAbs = Path.Combine(appDir, entry.Exec);
            string? iconAbs = entry.Icon != null ? Path.Combine(appDir, entry.Icon) : null;

            // A sandboxed app routes through `hina run` (launchOverride) so the
            // sandbox is installed before the app starts. The override is built by
            // InstallService from trusted values (app name + validated entry id).
            string execLine = launchOverride != null ? StripControl(launchOverride) : QuoteExec(execAbs);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[Desktop Entry]");
            sb.AppendLine("Type=Application");
            sb.AppendLine($"Name={Escape(entry.Name)}");
            sb.AppendLine($"Exec={execLine}");
            if (iconAbs != null) sb.AppendLine($"Icon={Escape(iconAbs)}");
            sb.AppendLine($"Terminal={(entry.Terminal ? "true" : "false")}");
            if (entry.Categories.Count > 0)
            {
                sb.AppendLine($"Categories={string.Join(";", entry.Categories.ConvertAll(StripControl))};");
            }
            sb.AppendLine("X-Hina-Managed=true");

            File.WriteAllText(targetPath, sb.ToString());
            return Task.FromResult(targetPath);
        }

        public Task RemoveMenuShortcut(string evidencePath, CancellationToken ct)
        {
            TryDeleteFile(evidencePath, _logger);
            return Task.CompletedTask;
        }

        // ---- AddToPath (symlink) ----

        public Task<string> AddToPath(string name, string targetExec, CancellationToken ct)
        {
            Directory.CreateDirectory(_userBinDir);

            string linkPath = Path.Combine(_userBinDir, name);

            // A real directory at the shim path is neither File.Exists (false on dirs) nor a
            // symlink (LinkTarget == null), so the cleanup guard below would skip it and
            // CreateSymbolicLink would throw a cryptic IOException — failing the whole install.
            // Surface a clear, actionable error instead (BUG-047). We do NOT recursively delete
            // a real directory: that would be destructive on user state.
            if (Directory.Exists(linkPath) && new FileInfo(linkPath).LinkTarget == null)
            {
                throw new IOException($"Cannot add '{name}' to PATH: '{linkPath}' is an existing directory. Remove it and retry.");
            }
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

        // ---- MIME type ----

        public Task<string> RegisterMimeType(MimeTypeHook hook, string appDir, string? entryExecAbs, CancellationToken ct)
        {
            string execValue = entryExecAbs ?? "";

            Directory.CreateDirectory(_userAppsDir);
            string fileName = $"hina-mime-{SanitizeId(hook.MimeType)}-{SanitizeId(hook.EntryId)}.desktop";
            string targetPath = Path.Combine(_userAppsDir, fileName);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[Desktop Entry]");
            sb.AppendLine("Type=Application");
            sb.AppendLine($"Name=Hina MIME {Escape(hook.MimeType)}");
            // Field codes (%f) must sit OUTSIDE the quoted executable: per the Desktop Entry
            // spec a %f inside double quotes is literal text, so the file path would never be
            // substituted. Quote only the exec, then append the field code unquoted.
            sb.AppendLine($"Exec={QuoteExec(execValue)} %f");
            sb.AppendLine("NoDisplay=true");
            sb.AppendLine($"MimeType={StripControl(hook.MimeType)};");
            sb.AppendLine($"X-Hina-Mime-Extensions={string.Join(",", hook.Extensions.ConvertAll(StripControl))}");
            sb.AppendLine("X-Hina-Managed=true");

            File.WriteAllText(targetPath, sb.ToString());
            return Task.FromResult(targetPath);
        }

        public Task UnregisterMimeType(string evidencePath, CancellationToken ct)
        {
            TryDeleteFile(evidencePath, _logger);
            return Task.CompletedTask;
        }

        // ---- URL scheme ----

        public Task<string> RegisterUrlScheme(UrlSchemeHook hook, string appDir, string? entryExecAbs, CancellationToken ct)
        {
            string execValue = entryExecAbs ?? "";

            Directory.CreateDirectory(_userAppsDir);
            string fileName = $"hina-url-{SanitizeId(hook.Scheme)}-{SanitizeId(hook.EntryId)}.desktop";
            string targetPath = Path.Combine(_userAppsDir, fileName);

            string mime = $"x-scheme-handler/{StripControl(hook.Scheme)}";

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[Desktop Entry]");
            sb.AppendLine("Type=Application");
            sb.AppendLine($"Name=Hina URL {Escape(hook.Scheme)}");
            // %u must sit outside the quotes — see RegisterMimeType.
            sb.AppendLine($"Exec={QuoteExec(execValue)} %u");
            sb.AppendLine("NoDisplay=true");
            sb.AppendLine($"MimeType={mime};");
            sb.AppendLine("X-Hina-Managed=true");

            File.WriteAllText(targetPath, sb.ToString());
            return Task.FromResult(targetPath);
        }

        public Task UnregisterUrlScheme(string evidencePath, CancellationToken ct)
        {
            TryDeleteFile(evidencePath, _logger);
            return Task.CompletedTask;
        }

        // ---- Font install ----

        public Task<string> InstallFont(string fontFile, CancellationToken ct)
        {
            // Legacy overload — no app context, so the file lands at its raw name.
            // New code paths route through the (file, appName, ct) overload below.
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
                    // Symlink missing OR its target file is gone.
                    if (!File.Exists(evidence) && new FileInfo(evidence).LinkTarget == null) return true;
                    string? target = new FileInfo(evidence).LinkTarget;
                    return target != null && !File.Exists(target);

                case "installFont":
                    foreach (string p in evidence.Split('|', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!File.Exists(p)) return true;
                    }
                    return false;

                case "registerMimeType":
                case "registerUrlScheme":
                case "registerAutostart":
                case "shellEntry":
                    return !File.Exists(evidence);

                default:
                    return !File.Exists(evidence);
            }
        }

        // ---- Autostart ----

        public Task<string> RegisterAutostart(AutostartHook hook, string appDir, string? entryExecAbs, CancellationToken ct)
        {
            string execValue = entryExecAbs ?? "";

            // Quote the executable and EACH argument independently. Joining args with spaces
            // and quoting the whole string as one token would (a) merge the exec + args into a
            // single quoted token the DE treats as one path, and (b) split any arg that itself
            // contains a space. Per-token quoting is the only spec-correct shape.
            StringBuilder execLine = new StringBuilder(QuoteExec(execValue));
            if (hook.Args is { Count: > 0 })
            {
                foreach (string arg in hook.Args)
                {
                    execLine.Append(' ').Append(QuoteExec(StripControl(arg)));
                }
            }

            Directory.CreateDirectory(_userAutostartDir);
            string fileName = $"hina-autostart-{SanitizeId(hook.EntryId)}.desktop";
            string targetPath = Path.Combine(_userAutostartDir, fileName);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[Desktop Entry]");
            sb.AppendLine("Type=Application");
            sb.AppendLine($"Name=Hina Autostart {Escape(hook.EntryId)}");
            sb.AppendLine($"Exec={execLine}");
            sb.AppendLine("X-GNOME-Autostart-enabled=true");
            sb.AppendLine("X-Hina-Managed=true");

            File.WriteAllText(targetPath, sb.ToString());
            return Task.FromResult(targetPath);
        }

        public Task UnregisterAutostart(string evidencePath, CancellationToken ct)
        {
            TryDeleteFile(evidencePath, _logger);
            return Task.CompletedTask;
        }

        // All Hina-managed artifacts on disk, by their `hina-*` filename marker: shortcuts +
        // mime/url handlers in the applications dir, autostart entries, and per-app fonts. Bin
        // symlinks are intentionally NOT scanned — they carry no Hina prefix, so distinguishing
        // them from the user's own symlinks safely isn't possible. `hina repair` subtracts the
        // registry-referenced paths from this to find true orphans.
        public IEnumerable<string> EnumerateManagedArtifacts()
        {
            List<string> found = new List<string>();
            AddManaged(found, _userAppsDir, "hina-*.desktop");
            AddManaged(found, _userAutostartDir, "hina-*.desktop");
            AddManaged(found, _userFontsDir, "hina-*");
            return found;
        }

        private static void AddManaged(List<string> into, string dir, string pattern)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    into.AddRange(Directory.EnumerateFiles(dir, pattern));
                }
            }
            catch { /* fail-soft: a scan error must not break repair */ }
        }

        // ---- Helpers ----

        private static string Escape(string value)
        {
            // Strip control chars first (defense in depth vs .desktop key injection — a raw CR/LF
            // would start a new key line), then escape backslash per the freedesktop spec.
            return StripControl(value).Replace("\\", "\\\\");
        }

        // Remove any control character so an interpolated value can't break out of its key line.
        // Quote a single Exec token per the freedesktop Desktop Entry spec. Reserved characters
        // require the token to be double-quoted; inside double quotes the characters
        // backslash, backtick, dollar and double-quote must each be escaped with a backslash.
        private static string QuoteExec(string path)
        {
            bool needsQuote = false;
            foreach (char c in path)
            {
                if (c == ' ' || c == '\t' || c == '"' || c == '\'' || c == '\\' ||
                    c == '>' || c == '<' || c == '~' || c == '|' || c == '&' || c == ';' ||
                    c == '$' || c == '*' || c == '?' || c == '#' || c == '(' || c == ')' || c == '`')
                {
                    needsQuote = true;
                    break;
                }
            }
            if (!needsQuote)
            {
                return path;
            }
            string escaped = path
                .Replace("\\", "\\\\")
                .Replace("`", "\\`")
                .Replace("$", "\\$")
                .Replace("\"", "\\\"");
            return "\"" + escaped + "\"";
        }

        private static string DefaultUserAppsDir()
        {
            string xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                             ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(xdgData, "applications");
        }

        private static string DefaultUserFontsDir()
        {
            string xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                             ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(xdgData, "fonts");
        }

        private static string DefaultUserAutostartDir()
        {
            string xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                               ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(xdgConfig, "autostart");
        }
    }
}
