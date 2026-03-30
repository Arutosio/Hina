using System;
using System.IO;
using Hina.Core.Patching;
using Xunit;

namespace Hina.Core.Tests
{
    public class PatchCleanupTests
    {
        [Fact]
        public void Cleanup_RemovesTmpFiles()
        {
            string tempDir = CreateTempDir();
            try
            {
                File.WriteAllText(Path.Combine(tempDir, "data.hina.tmp"), "temp");
                File.WriteAllText(Path.Combine(tempDir, "keep.txt"), "keep");

                PatchCleanup.Cleanup(tempDir);

                Assert.False(File.Exists(Path.Combine(tempDir, "data.hina.tmp")));
                Assert.True(File.Exists(Path.Combine(tempDir, "keep.txt")));
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void Cleanup_RemovesBakFiles()
        {
            string tempDir = CreateTempDir();
            try
            {
                File.WriteAllText(Path.Combine(tempDir, "app.exe.hina.bak"), "backup");

                PatchCleanup.Cleanup(tempDir);

                Assert.False(File.Exists(Path.Combine(tempDir, "app.exe.hina.bak")));
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void Cleanup_RemovesJournalFile()
        {
            string tempDir = CreateTempDir();
            try
            {
                string journalDir = Path.Combine(tempDir, ".hina");
                Directory.CreateDirectory(journalDir);
                File.WriteAllText(Path.Combine(journalDir, "journal.json"), "{}");

                PatchCleanup.Cleanup(tempDir);

                Assert.False(File.Exists(Path.Combine(journalDir, "journal.json")));
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void Cleanup_RemovesInSubdirectories()
        {
            string tempDir = CreateTempDir();
            try
            {
                string subDir = Path.Combine(tempDir, "sub", "deep");
                Directory.CreateDirectory(subDir);
                File.WriteAllText(Path.Combine(subDir, "file.hina.tmp"), "temp");
                File.WriteAllText(Path.Combine(subDir, "file.hina.bak"), "bak");
                File.WriteAllText(Path.Combine(subDir, "real.dll"), "real");

                PatchCleanup.Cleanup(tempDir);

                Assert.False(File.Exists(Path.Combine(subDir, "file.hina.tmp")));
                Assert.False(File.Exists(Path.Combine(subDir, "file.hina.bak")));
                Assert.True(File.Exists(Path.Combine(subDir, "real.dll")));
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void Cleanup_NonExistentDirectory_DoesNotThrow()
        {
            string fakePath = Path.Combine(Path.GetTempPath(), "hina-nonexistent-" + Guid.NewGuid().ToString("N"));
            PatchCleanup.Cleanup(fakePath); // Should not throw
        }

        [Fact]
        public void Cleanup_EmptyDirectory_DoesNotThrow()
        {
            string tempDir = CreateTempDir();
            try
            {
                PatchCleanup.Cleanup(tempDir); // No files to clean
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void Cleanup_PreservesNonArtifactFiles()
        {
            string tempDir = CreateTempDir();
            try
            {
                File.WriteAllText(Path.Combine(tempDir, "app.exe"), "exe");
                File.WriteAllText(Path.Combine(tempDir, "config.json"), "{}");
                File.WriteAllText(Path.Combine(tempDir, "data.tmp"), "not a hina tmp");

                PatchCleanup.Cleanup(tempDir);

                Assert.True(File.Exists(Path.Combine(tempDir, "app.exe")));
                Assert.True(File.Exists(Path.Combine(tempDir, "config.json")));
                Assert.True(File.Exists(Path.Combine(tempDir, "data.tmp"))); // .tmp without .hina prefix
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        private static string CreateTempDir()
        {
            string path = Path.Combine(Path.GetTempPath(), "hina-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
