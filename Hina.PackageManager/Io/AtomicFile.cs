using System.IO;
using System.Text;

namespace Hina.PackageManager.Io
{
    // Crash-safe text write: serialize to a sibling .tmp, flush to disk, then
    // atomically rename over the target. A crash mid-write leaves the old file
    // intact (or no file) — never a truncated one. Same pattern RegistryStore
    // uses for registry.json; centralised so the descriptor cache and any future
    // small-file writes share it.
    public static class AtomicFile
    {
        public static void WriteAllText(string path, string content)
        {
            string dir = Path.GetDirectoryName(path) ?? ".";
            Directory.CreateDirectory(dir);

            string tmp = path + ".tmp";
            using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(content);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            // Atomic on POSIX and Windows for a same-volume rename.
            File.Move(tmp, path, overwrite: true);
        }
    }
}
