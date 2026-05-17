using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Paths;

namespace Hina.PackageManager.Platform.Linux
{
    // Linux shell integration. Phase 2 implements menu shortcuts (.desktop) and AddToPath (symlink).
    // Other hooks (MIME, font, scheme, autostart) arrive in Phase 4.
    public sealed class LinuxPlatformIntegration : IPlatformIntegration
    {
        private readonly string _userBinDir;
        private readonly string _userAppsDir;

        public LinuxPlatformIntegration(InstallPaths paths)
            : this(paths.UserBinDir, DefaultUserAppsDir())
        {
        }

        // Test seam: caller controls every directory we touch.
        public LinuxPlatformIntegration(string userBinDir, string userAppsDir)
        {
            _userBinDir = userBinDir;
            _userAppsDir = userAppsDir;
        }

        public string OsId => "linux";
        public string UserBinDir => _userBinDir;
        public string UserAppsDir => _userAppsDir;

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

        public Task<string> AddToPath(string name, string targetExec, CancellationToken ct)
        {
            Directory.CreateDirectory(_userBinDir);

            string linkPath = Path.Combine(_userBinDir, name);

            // If a stale link / file exists at that name, remove it so the symlink call doesn't fail.
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

        public Task<string> RegisterMimeType(MimeTypeHook hook, string appDir, CancellationToken ct) => throw Phase4();
        public Task UnregisterMimeType(string evidencePath, CancellationToken ct) => Task.CompletedTask;
        public Task<string> RegisterUrlScheme(UrlSchemeHook hook, string appDir, CancellationToken ct) => throw Phase4();
        public Task UnregisterUrlScheme(string evidencePath, CancellationToken ct) => Task.CompletedTask;
        public Task<string> InstallFont(string fontFile, CancellationToken ct) => throw Phase4();
        public Task UninstallFont(string evidencePath, CancellationToken ct) => Task.CompletedTask;
        public Task<string> RegisterAutostart(AutostartHook hook, string appDir, CancellationToken ct) => throw Phase4();
        public Task UnregisterAutostart(string evidencePath, CancellationToken ct) => Task.CompletedTask;

        private static PlatformNotSupportedException Phase4() =>
            new PlatformNotSupportedException("This hook arrives in Phase 4.");

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

        // Desktop-entry spec reserves a small set of characters; escape conservatively.
        private static string Escape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\n", "\\n");
        }

        private static string QuoteExec(string path)
        {
            // Desktop-entry Exec field: quote when path contains a space, escape embedded quotes.
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
    }
}
