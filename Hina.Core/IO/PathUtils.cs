using System.IO;

namespace Hina.Core.IO
{
    internal static class PathUtils
    {
        public static string NormalizeRelativePath(string path)
        {
            return path.Replace('\\', '/');
        }

        public static string ToOsPath(string rootDir, string manifestPath)
        {
            string rel = manifestPath.Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(rootDir, rel);
        }
    }
}
