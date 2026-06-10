using System;
using System.IO;
using System.Linq;
using Hina.Core.Inputs;
using Xunit;

namespace Hina.Core.Tests
{
    public class InputSetTests : IDisposable
    {
        private readonly string _tempDir;

        public InputSetTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "hina-inputset-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        }

        private DirectoryInfo Root(string name, params (string RelPath, string Content)[] files)
        {
            string root = Path.Combine(_tempDir, name);
            foreach ((string rel, string content) in files)
            {
                string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
            }
            Directory.CreateDirectory(root);
            return new DirectoryInfo(root);
        }

        [Fact]
        public void Resolve_SingleRoot_ListsAllFilesSortedByRelativePath_WithForwardSlashes()
        {
            DirectoryInfo root = Root("single",
                ("sub/dir/b.dat", "b"),
                ("a.txt", "a"),
                ("sub/c.bin", "c"));

            InputSet set = InputSet.Resolve(new[] { root });

            Assert.Equal(new[] { "a.txt", "sub/c.bin", "sub/dir/b.dat" }, set.Files.Select(f => f.RelativePath));
            Assert.All(set.Files, f => Assert.DoesNotContain('\\', f.RelativePath));
            Assert.All(set.Files, f => Assert.True(File.Exists(f.AbsolutePath)));
            Assert.All(set.Files, f => Assert.Equal(root.FullName, f.SourceRoot));
            Assert.Empty(set.Overrides);
            Assert.Empty(set.CaseClashes);
        }

        [Fact]
        public void Resolve_MissingRoot_ThrowsDirectoryNotFound()
        {
            DirectoryInfo missing = new DirectoryInfo(Path.Combine(_tempDir, "nope"));
            Assert.Throws<DirectoryNotFoundException>(() => InputSet.Resolve(new[] { missing }));
        }

        [Fact]
        public void Resolve_TwoRoots_DisjointSets_MergesAll_NoOverrides()
        {
            DirectoryInfo common = Root("common", ("data/map0.mul", "map"), ("art.mul", "art"));
            DirectoryInfo variant = Root("variant", ("game.exe", "exe"));

            InputSet set = InputSet.Resolve(new[] { common, variant });

            Assert.Equal(new[] { "art.mul", "data/map0.mul", "game.exe" }, set.Files.Select(f => f.RelativePath));
            Assert.Empty(set.Overrides);
        }

        [Fact]
        public void Resolve_SameRelativePathInBothRoots_LaterRootWins_AndOverrideRecorded()
        {
            DirectoryInfo common = Root("common", ("config.ini", "common-version"));
            DirectoryInfo variant = Root("variant", ("config.ini", "variant-version"));

            InputSet set = InputSet.Resolve(new[] { common, variant });

            InputFile file = Assert.Single(set.Files);
            Assert.Equal("config.ini", file.RelativePath);
            Assert.Equal(variant.FullName, file.SourceRoot);
            Assert.Equal("variant-version", File.ReadAllText(file.AbsolutePath));

            InputOverride ov = Assert.Single(set.Overrides);
            Assert.Equal("config.ini", ov.RelativePath);
            Assert.Equal(variant.FullName, ov.WinningRoot);
            Assert.Equal(common.FullName, ov.OverriddenRoot);
        }

        [Fact]
        public void Resolve_EmptyCommonRoot_YieldsVariantFilesOnly()
        {
            DirectoryInfo common = Root("common");
            DirectoryInfo variant = Root("variant", ("game.exe", "exe"));

            InputSet set = InputSet.Resolve(new[] { common, variant });

            Assert.Equal(new[] { "game.exe" }, set.Files.Select(f => f.RelativePath));
            Assert.Empty(set.Overrides);
        }

        [Fact]
        public void Resolve_NestedPathCollision_DetectedAcrossSubdirectories()
        {
            DirectoryInfo common = Root("common", ("data/maps/map0.mul", "old"));
            DirectoryInfo variant = Root("variant", ("data/maps/map0.mul", "new"));

            InputSet set = InputSet.Resolve(new[] { common, variant });

            InputFile file = Assert.Single(set.Files);
            Assert.Equal("data/maps/map0.mul", file.RelativePath);
            Assert.Equal("new", File.ReadAllText(file.AbsolutePath));
            Assert.Single(set.Overrides);
        }

        [Fact]
        public void Resolve_CaseDifferingPaths_RemainDistinctFiles_ButReportedAsCaseClash()
        {
            // Distinct ordinal keys (Linux-correct: two different files), but on a
            // case-insensitive filesystem (Windows/macOS) they would land on the same
            // installed path - the set must surface that hazard.
            DirectoryInfo common = Root("common", ("Data/readme.txt", "upper"));
            DirectoryInfo variant = Root("variant", ("data/readme.txt", "lower"));

            InputSet set = InputSet.Resolve(new[] { common, variant });

            Assert.Equal(2, set.Files.Count);
            Assert.Empty(set.Overrides);
            string clash = Assert.Single(set.CaseClashes);
            Assert.Equal("data/readme.txt", clash, ignoreCase: true);
        }

        [Fact]
        public void Resolve_DeterministicOrder_SortedOrdinal()
        {
            // Ordinal sort: uppercase 'Z' (0x5A) sorts before lowercase 'a' (0x61).
            DirectoryInfo root = Root("order", ("a.txt", "1"), ("Z.txt", "2"));

            InputSet set = InputSet.Resolve(new[] { root });

            Assert.Equal(new[] { "Z.txt", "a.txt" }, set.Files.Select(f => f.RelativePath));
        }
    }
}
