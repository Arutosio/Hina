using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Hina.Core.Inputs
{
    // A relative path present in more than one root; the later root's copy won.
    public sealed class InputOverride
    {
        public string RelativePath { get; init; } = string.Empty;
        public string WinningRoot { get; init; } = string.Empty;
        public string OverriddenRoot { get; init; } = string.Empty;
    }

    // Snapshot of the files to package, merged from one or more input roots.
    // LATER roots win on a relative-path collision (variant-wins: pass the
    // shared/common root first, the variant root last).
    //
    // Both the manifest builder and the chunk-store writer consume the SAME
    // resolved set, so the manifest and the store can never disagree about
    // which files exist (each used to enumerate the directory independently).
    public sealed class InputSet
    {
        // Sorted by RelativePath, StringComparer.Ordinal: deterministic
        // manifests regardless of filesystem enumeration order.
        public IReadOnlyList<InputFile> Files { get; }

        public IReadOnlyList<InputOverride> Overrides { get; }

        // Relative paths that are distinct under ordinal comparison but would
        // collide on a case-insensitive filesystem (Windows/macOS installs).
        // Merging is ordinal so Linux semantics stay correct; callers should
        // surface these as a warning.
        public IReadOnlyList<string> CaseClashes { get; }

        private InputSet(IReadOnlyList<InputFile> files, IReadOnlyList<InputOverride> overrides, IReadOnlyList<string> caseClashes)
        {
            Files = files;
            Overrides = overrides;
            CaseClashes = caseClashes;
        }

        public static InputSet Resolve(IReadOnlyList<DirectoryInfo> rootsInPrecedenceOrder)
        {
            if (rootsInPrecedenceOrder == null || rootsInPrecedenceOrder.Count == 0)
            {
                throw new ArgumentException("At least one input root is required.", nameof(rootsInPrecedenceOrder));
            }

            Dictionary<string, InputFile> byRelPath = new Dictionary<string, InputFile>(StringComparer.Ordinal);
            List<InputOverride> overrides = new List<InputOverride>();

            foreach (DirectoryInfo root in rootsInPrecedenceOrder)
            {
                if (!root.Exists)
                {
                    throw new DirectoryNotFoundException(root.FullName);
                }

                foreach (string filePath in Directory.EnumerateFiles(root.FullName, "*", SearchOption.AllDirectories))
                {
                    string relPath = Path.GetRelativePath(root.FullName, filePath).Replace('\\', '/');
                    InputFile entry = new InputFile
                    {
                        AbsolutePath = filePath,
                        RelativePath = relPath,
                        SourceRoot = root.FullName
                    };

                    if (byRelPath.TryGetValue(relPath, out InputFile? previous))
                    {
                        overrides.Add(new InputOverride
                        {
                            RelativePath = relPath,
                            WinningRoot = root.FullName,
                            OverriddenRoot = previous.SourceRoot
                        });
                    }
                    byRelPath[relPath] = entry;
                }
            }

            List<InputFile> files = byRelPath.Values
                .OrderBy(f => f.RelativePath, StringComparer.Ordinal)
                .ToList();

            List<string> caseClashes = files
                .GroupBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            overrides.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));

            return new InputSet(files, overrides, caseClashes);
        }
    }
}
