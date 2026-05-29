using System;
using System.IO;
using System.Text;

namespace Hina.PackageManager.Platform
{
    // Helpers shared by the per-OS IPlatformIntegration implementations. Each OS speaks a
    // different shell-integration dialect, but file deletion and id/filename sanitization are
    // identical across them — keeping one copy avoids the three-way drift flagged in the
    // architecture review (#4).
    internal static class PlatformText
    {
        // Fail-soft delete that also removes dangling symlinks (a symlink whose target is gone
        // reports File.Exists == false, but LinkTarget != null). Uninstall must not abort on a
        // missing/locked file, so all failures are swallowed.
        public static void TryDeleteFile(string path)
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

        // Sanitize a descriptor id into a freedesktop/bundle-safe token: letters, digits, '-'
        // and '_' pass through; anything else becomes '-'. Empty input falls back to "entry".
        public static string SanitizeId(string id)
        {
            StringBuilder sb = new StringBuilder(id.Length);
            foreach (char c in id)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
                else sb.Append('-');
            }
            return sb.Length == 0 ? "entry" : sb.ToString();
        }

        // Sanitize an arbitrary string into a filesystem-safe file name: OS-invalid chars become
        // '_'. Empty/whitespace-only input falls back to "Hina".
        public static string SanitizeFileName(string value)
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
    }
}
