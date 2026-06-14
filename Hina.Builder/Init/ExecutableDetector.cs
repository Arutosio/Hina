using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Hina.Builder.Init
{
    internal enum TargetOs
    {
        Windows,
        Linux,
        Macos
    }

    // One executable candidate found in the build folder.
    internal sealed class DetectedExecutable
    {
        public string RelPath { get; init; } = string.Empty;   // forward-slash, relative to the scan root
        public TargetOs Os { get; init; }
        public bool IsBundle { get; init; }                     // macOS .app
        // Higher = more likely to be the real app entry (vs a bundled library).
        public int Rank { get; init; }
    }

    // Classifies files by magic bytes (PE / ELF / Mach-O) and recognises macOS .app bundles,
    // so the wizard can propose the executables instead of asking the user to type paths.
    // Pure + no interactive I/O: feed it a directory, get a ranked list back.
    internal static class ExecutableDetector
    {
        public static IReadOnlyList<DetectedExecutable> Detect(DirectoryInfo root)
        {
            List<DetectedExecutable> found = new List<DetectedExecutable>();
            if (!root.Exists)
            {
                return found;
            }

            // macOS .app bundles first — once matched, skip their inner files so we don't also
            // list the inner Mach-O as a separate candidate.
            HashSet<string> consumed = new HashSet<string>(StringComparer.Ordinal);
            foreach (DirectoryInfo dir in root.EnumerateDirectories("*.app", SearchOption.AllDirectories))
            {
                string? innerExec = FindBundleExec(dir);
                // If the bundle has no Contents/MacOS executable, treat it as unrecognised:
                // do not add the directory path as a candidate and do not consume the inner
                // files, so any Mach-O inside remains visible to the flat scan below.
                if (innerExec == null)
                {
                    continue;
                }
                string rel = ToRel(root, innerExec);
                found.Add(new DetectedExecutable { RelPath = rel, Os = TargetOs.Macos, IsBundle = true, Rank = 100 });
                foreach (FileInfo f in dir.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    consumed.Add(f.FullName);
                }
            }

            foreach (FileInfo file in root.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (consumed.Contains(file.FullName))
                {
                    continue;
                }
                TargetOs? os = ClassifyByMagic(file.FullName);
                if (os == null)
                {
                    continue;
                }
                found.Add(new DetectedExecutable
                {
                    RelPath = ToRel(root, file.FullName),
                    Os = os.Value,
                    IsBundle = false,
                    Rank = RankFor(file.Name, os.Value, root)
                });
            }

            // Best candidate first: higher rank, then shallower path, then shorter name.
            return found
                .OrderByDescending(e => e.Rank)
                .ThenBy(e => e.RelPath.Count(c => c == '/'))
                .ThenBy(e => e.RelPath.Length)
                .ToList();
        }

        private static string? FindBundleExec(DirectoryInfo bundle)
        {
            string macOsDir = Path.Combine(bundle.FullName, "Contents", "MacOS");
            if (!Directory.Exists(macOsDir))
            {
                return null;
            }
            // Only a real Mach-O under Contents/MacOS is the bundle exec. If there is none
            // (malformed bundle: only data/plist/text files), treat the bundle as unrecognised
            // rather than proposing an arbitrary, possibly non-executable, non-deterministic
            // files[0] as a top-rank default (BUG-037).
            string[] files = Directory.GetFiles(macOsDir);
            return files.FirstOrDefault(f => ClassifyByMagic(f) == TargetOs.Macos);
        }

        private static TargetOs? ClassifyByMagic(string path)
        {
            byte[] head = new byte[4];
            int read;
            try
            {
                using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                read = fs.Read(head, 0, 4);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            if (read < 4)
            {
                return null;
            }

            // PE (Windows): "MZ"
            if (head[0] == 0x4D && head[1] == 0x5A)
            {
                return TargetOs.Windows;
            }
            // ELF (Linux): 0x7F 'E' 'L' 'F'
            if (head[0] == 0x7F && head[1] == 0x45 && head[2] == 0x4C && head[3] == 0x46)
            {
                return TargetOs.Linux;
            }
            // Mach-O (macOS): thin (BE/LE, 32/64) or fat/universal.
            uint magic = (uint)((head[0] << 24) | (head[1] << 16) | (head[2] << 8) | head[3]);
            if (magic == 0xFEEDFACE || magic == 0xFEEDFACF // big-endian 32/64
                || magic == 0xCEFAEDFE || magic == 0xCFFAEDFE // little-endian 32/64
                || magic == 0xCAFEBABE || magic == 0xCAFEBABF) // fat/universal
            {
                return TargetOs.Macos;
            }
            return null;
        }

        // Real app entries outrank bundled libraries. Libraries keep magic bytes too (a .so is
        // ELF, a .dll is PE), so demote the usual library extensions.
        private static int RankFor(string fileName, TargetOs os, DirectoryInfo root)
        {
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (ext is ".dll" or ".so" or ".dylib" or ".a" or ".lib")
            {
                return 1;
            }

            int rank = 10;
            if (os == TargetOs.Windows && ext == ".exe")
            {
                rank += 20;
            }
            if (os != TargetOs.Windows && ext.Length == 0)
            {
                // Unix executables usually have no extension.
                rank += 20;
            }
            // Name matching the folder is very likely the main entry (e.g. MyGame/MyGame).
            if (string.Equals(Path.GetFileNameWithoutExtension(fileName), root.Name, StringComparison.OrdinalIgnoreCase))
            {
                rank += 15;
            }
            return rank;
        }

        private static string ToRel(DirectoryInfo root, string fullPath)
        {
            string rel = Path.GetRelativePath(root.FullName, fullPath);
            return rel.Replace('\\', '/');
        }
    }
}
