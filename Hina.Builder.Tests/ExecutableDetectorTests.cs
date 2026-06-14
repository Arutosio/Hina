using System;
using System.IO;
using System.Linq;
using Hina.Builder.Init;

namespace Hina.Builder.Tests
{
    public sealed class ExecutableDetectorTests : IDisposable
    {
        private readonly string _root;

        public ExecutableDetectorTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "hina-detect-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }

        private void Write(string rel, byte[] header)
        {
            string path = Path.Combine(_root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            byte[] body = new byte[64];
            Array.Copy(header, body, header.Length);
            File.WriteAllBytes(path, body);
        }

        private static readonly byte[] Pe = { 0x4D, 0x5A, 0x90, 0x00 };
        private static readonly byte[] Elf = { 0x7F, 0x45, 0x4C, 0x46 };
        private static readonly byte[] MachO = { 0xCF, 0xFA, 0xED, 0xFE };

        [Fact]
        public void Detect_ClassifiesByMagicBytes()
        {
            Write("game.exe", Pe);
            Write("game", Elf);
            Write("game-mac", MachO);
            File.WriteAllText(Path.Combine(_root, "readme.txt"), "not an executable");

            var found = ExecutableDetector.Detect(new DirectoryInfo(_root));

            Assert.Contains(found, e => e.RelPath == "game.exe" && e.Os == TargetOs.Windows);
            Assert.Contains(found, e => e.RelPath == "game" && e.Os == TargetOs.Linux);
            Assert.Contains(found, e => e.RelPath == "game-mac" && e.Os == TargetOs.Macos);
            Assert.DoesNotContain(found, e => e.RelPath == "readme.txt");
        }

        [Fact]
        public void Detect_RanksMainExeAboveLibraries()
        {
            Write("mylib.dll", Pe);
            Write("game.exe", Pe);

            var found = ExecutableDetector.Detect(new DirectoryInfo(_root));

            // .exe should rank ahead of the .dll library.
            Assert.Equal("game.exe", found.First(e => e.Os == TargetOs.Windows).RelPath);
            Assert.True(found.First(e => e.RelPath == "game.exe").Rank
                        > found.First(e => e.RelPath == "mylib.dll").Rank);
        }

        [Fact]
        public void Detect_RecognisesMacAppBundle()
        {
            string inner = Path.Combine("MyGame.app", "Contents", "MacOS", "MyGame");
            Write(inner, MachO);

            var found = ExecutableDetector.Detect(new DirectoryInfo(_root));

            var bundle = found.FirstOrDefault(e => e.IsBundle);
            Assert.NotNull(bundle);
            Assert.Equal(TargetOs.Macos, bundle!.Os);
            Assert.Equal("MyGame.app/Contents/MacOS/MyGame", bundle.RelPath);
            // The inner Mach-O is not also listed as a separate plain candidate.
            Assert.Single(found, e => e.Os == TargetOs.Macos);
        }

        [Fact]
        public void Detect_AppBundleWithoutContentsMacOS_DoesNotSuppressMachOInside()
        {
            // A malformed .app that lacks the Contents/MacOS directory — FindBundleExec
            // returns null. The Mach-O inside must still surface as a plain candidate and
            // the bundle directory path must NOT be proposed as an executable.
            string macho = Path.Combine("Broken.app", "Resources", "game");
            Write(macho, MachO);

            var found = ExecutableDetector.Detect(new DirectoryInfo(_root));

            // The inner Mach-O is visible as a normal candidate.
            Assert.Contains(found, e => e.RelPath == "Broken.app/Resources/game" && e.Os == TargetOs.Macos);
            // No entry whose RelPath points at the bundle directory itself.
            Assert.DoesNotContain(found, e => e.IsBundle);
            Assert.DoesNotContain(found, e => e.RelPath == "Broken.app" || e.RelPath == "Broken.app/");
        }

        [Fact]
        public void Detect_AppBundleWithoutContentsMacOS_DirectoryPathNotProposedAsExecutable()
        {
            // Belt-and-suspenders check: no candidate should ever carry a RelPath that
            // resolves to a directory rather than a file, even when the bundle is malformed.
            string macho = Path.Combine("Empty.app", "bin", "myexe");
            Write(macho, MachO);

            var found = ExecutableDetector.Detect(new DirectoryInfo(_root));

            foreach (var candidate in found)
            {
                string fullPath = Path.Combine(_root, candidate.RelPath.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(fullPath),
                    $"Candidate '{candidate.RelPath}' does not point to a file.");
            }
        }
    }
}
