using System;
using System.IO;

namespace Hina.Core.IO
{
    internal static class PathUtils
    {
        public static string NormalizeRelativePath(string path)
        {
            return path.Replace('\\', '/');
        }

        // Resolve a manifest-relative path against rootDir, REFUSING any path that escapes
        // rootDir. manifest.Files[].Path is publisher-controlled (and, on an unsigned or
        // MITM'd manifest, attacker-controlled); without this guard a "../../etc/..." or an
        // absolute path would let PatchClient write/read outside the install directory.
        public static string ToOsPath(string rootDir, string manifestPath)
        {
            if (manifestPath is null)
                throw new ArgumentNullException(nameof(manifestPath), "Manifest path must not be null.");

            string rel = manifestPath.Replace('/', Path.DirectorySeparatorChar);
            string rootFull = Path.GetFullPath(rootDir);
            string full = Path.GetFullPath(Path.Combine(rootFull, rel));

            // Path.Combine returns `rel` verbatim when it is rooted, so an absolute manifest
            // path escapes here too — the containment check below catches both cases.
            if (full != rootFull &&
                !full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Manifest path '{manifestPath}' resolves outside the target directory and was rejected.");
            }

            return full;
        }
    }
}
