using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Paths;

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

        public LinuxPlatformIntegration(InstallPaths paths)
            : this(paths.UserBinDir, DefaultUserAppsDir(), DefaultUserFontsDir(), DefaultUserAutostartDir())
        {
        }

        // Test seam: caller controls every directory we touch.
        public LinuxPlatformIntegration(string userBinDir, string userAppsDir, string? userFontsDir = null, string? userAutostartDir = null)
        {
            _userBinDir = userBinDir;
            _userAppsDir = userAppsDir;
            _userFontsDir = userFontsDir ?? DefaultUserFontsDir();
            _userAutostartDir = userAutostartDir ?? DefaultUserAutostartDir();
        }

        public string OsId => "linux";
        public string UserBinDir => _userBinDir;
        public string UserAppsDir => _userAppsDir;

        // ---- Menu shortcut ----

        public Task<string> CreateMenuShortcut(ShellEntry entry, string appDir, CancellationToken ct)
        {
            Directory.CreateDirectory(_userAppsDir);

            string fileName = $"hina-{SanitizeId(entry.Id)}.desktop";
            string targetPath = Path.Combine(_userAppsDir, fileName);

            string execAbs = Path.Combine(appDir, entry.Exec);
            string? iconAbs = entry.Icon != null ? Path.Combine(appDir, entry.Icon) : null;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[Desktop Entry]");
            sb.AppendLine("Type=Application");
            sb.AppendLine($"Name={Escape(entry.Name)}");
            sb.AppendLine($"Exec={QuoteExec(execAbs)}");
            if (iconAbs != null) sb.AppendLine($"Icon={Escape(iconAbs)}");
            sb.AppendLine($"Terminal={(entry.Terminal ? "true" : "false")}");
            if (entry.Categories.Count > 0)
            {
                sb.AppendLine($"Categories={string.Join(";", entry.Categories)};");
            }
            sb.AppendLine("X-Hina-Managed=true");

            File.WriteAllText(targetPath, sb.ToString());
            return Task.FromResult(targetPath);
        }

        public Task RemoveMenuShortcut(string evidencePath, CancellationToken ct)
        {
            TryDeleteFile(evidencePath);
            return Task.CompletedTask;
        }

        // ---- AddToPath (symlink) ----

        public Task<string> AddToPath(string name, string targetExec, CancellationToken ct)
        {
            Directory.CreateDirectory(_userBinDir);

            string linkPath = Path.Combine(_userBinDir, name);

            if (File.Exists(linkPath) || new FileInfo(linkPath).LinkTarget != null)
            {
                TryDeleteFile(linkPath);
            }

            File.CreateSymbolicLink(linkPath, targetExec);
            return Task.FromResult(linkPath);
        }

        public Task RemoveFromPath(string evidencePath, CancellationToken ct)
        {
            TryDeleteFile(evidencePath);
            return Task.CompletedTask;
        }

        // ---- MIME type ----

        public Task<string> RegisterMimeType(MimeTypeHook hook, string appDir, CancellationToken ct)
        {
            ShellEntry? targetEntry = FindEntry(hook.EntryId);
            string execValue = targetEntry != null ? Path.Combine(appDir, targetEntry.Exec) : "";

            Directory.CreateDirectory(_userAppsDir);
            string fileName = $"hina-mime-{SanitizeId(hook.MimeType)}-{SanitizeId(hook.EntryId)}.desktop";
            string targetPath = Path.Combine(_userAppsDir, fileName);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[Desktop Entry]");
            sb.AppendLine("Type=Application");
            sb.AppendLine($"Name=Hina MIME {Escape(hook.MimeType)}");
            sb.AppendLine($"Exec={QuoteExec(execValue + " %f")}");
            sb.AppendLine("NoDisplay=true");
            sb.AppendLine($"MimeType={hook.MimeType};");
            sb.AppendLine($"X-Hina-Mime-Extensions={string.Join(",", hook.Extensions)}");
            sb.AppendLine("X-Hina-Managed=true");

            File.WriteAllText(targetPath, sb.ToString());
            return Task.FromResult(targetPath);
        }

        public Task UnregisterMimeType(string evidencePath, CancellationToken ct)
        {
            TryDeleteFile(evidencePath);
            return Task.CompletedTask;
        }

        // ---- URL scheme ----

        public Task<string> RegisterUrlScheme(UrlSchemeHook hook, string appDir, CancellationToken ct)
        {
            ShellEntry? targetEntry = FindEntry(hook.EntryId);
            string execValue = targetEntry != null ? Path.Combine(appDir, targetEntry.Exec) : "";

            Directory.CreateDirectory(_userAppsDir);
            string fileName = $"hina-url-{SanitizeId(hook.Scheme)}-{SanitizeId(hook.EntryId)}.desktop";
            string targetPath = Path.Combine(_userAppsDir, fileName);

            string mime = $"x-scheme-handler/{hook.Scheme}";

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[Desktop Entry]");
            sb.AppendLine("Type=Application");
            sb.AppendLine($"Name=Hina URL {Escape(hook.Scheme)}");
            sb.AppendLine($"Exec={QuoteExec(execValue + " %u")}");
            sb.AppendLine("NoDisplay=true");
            sb.AppendLine($"MimeType={mime};");
            sb.AppendLine("X-Hina-Managed=true");

            File.WriteAllText(targetPath, sb.ToString());
            return Task.FromResult(targetPath);
        }

        public Task UnregisterUrlScheme(string evidencePath, CancellationToken ct)
        {
            TryDeleteFile(evidencePath);
            return Task.CompletedTask;
        }

        // ---- Font install ----

        public Task<string> InstallFont(string fontFile, CancellationToken ct)
        {
            Directory.CreateDirectory(_userFontsDir);
            string destPath = Path.Combine(_userFontsDir, Path.GetFileName(fontFile));
            File.Copy(fontFile, destPath, overwrite: true);
            return Task.FromResult(destPath);
        }

        public Task UninstallFont(string evidencePath, CancellationToken ct)
        {
            TryDeleteFile(evidencePath);
            return Task.CompletedTask;
        }

        // ---- Autostart ----

        public Task<string> RegisterAutostart(AutostartHook hook, string appDir, CancellationToken ct)
        {
            ShellEntry? targetEntry = FindEntry(hook.EntryId);
            string execValue = targetEntry != null ? Path.Combine(appDir, targetEntry.Exec) : "";
            if (hook.Args is { Count: > 0 })
            {
                execValue = execValue + " " + string.Join(" ", hook.Args);
            }

            Directory.CreateDirectory(_userAutostartDir);
            string fileName = $"hina-autostart-{SanitizeId(hook.EntryId)}.desktop";
            string targetPath = Path.Combine(_userAutostartDir, fileName);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[Desktop Entry]");
            sb.AppendLine("Type=Application");
            sb.AppendLine($"Name=Hina Autostart {Escape(hook.EntryId)}");
            sb.AppendLine($"Exec={QuoteExec(execValue)}");
            sb.AppendLine("X-GNOME-Autostart-enabled=true");
            sb.AppendLine("X-Hina-Managed=true");

            File.WriteAllText(targetPath, sb.ToString());
            return Task.FromResult(targetPath);
        }

        public Task UnregisterAutostart(string evidencePath, CancellationToken ct)
        {
            TryDeleteFile(evidencePath);
            return Task.CompletedTask;
        }

        // ---- Helpers ----

        // The platform layer doesn't have access to the descriptor's entries[]. For
        // hook .desktop files we leave Exec empty when the entry can't be resolved;
        // callers wishing to enforce strict resolution should pass a richer hook
        // shape in a future revision.
        private static ShellEntry? FindEntry(string id) => null;

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path) || new FileInfo(path).LinkTarget != null)
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Fail-soft: uninstall must not abort on missing/locked files.
            }
        }

        private static string Escape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\n", "\\n");
        }

        private static string QuoteExec(string path)
        {
            if (path.IndexOf(' ') < 0 && path.IndexOf('"') < 0)
            {
                return path;
            }
            return "\"" + path.Replace("\"", "\\\"") + "\"";
        }

        private static string SanitizeId(string id)
        {
            StringBuilder sb = new StringBuilder(id.Length);
            foreach (char c in id)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
                else sb.Append('-');
            }
            return sb.Length == 0 ? "entry" : sb.ToString();
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
