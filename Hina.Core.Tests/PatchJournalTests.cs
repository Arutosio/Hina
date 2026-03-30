using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Hina.Core.Patching;
using Xunit;

namespace Hina.Core.Tests
{
    public class PatchJournalTests
    {
        [Fact]
        public void GetJournalPath_ReturnsExpectedPath()
        {
            string root = "/some/root";
            string result = PatchJournal.GetJournalPath(root);

            Assert.Equal(Path.Combine("/some/root", ".hina", "journal.json"), result);
        }

        [Fact]
        public void Load_NonExistentFile_ReturnsNull()
        {
            string result = PatchJournal.GetJournalPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
            PatchJournal? journal = PatchJournal.Load(result);

            Assert.Null(journal);
        }

        [Fact]
        public async Task SaveAsync_Load_RoundTrip()
        {
            string tempDir = CreateTempDir();
            try
            {
                string journalPath = PatchJournal.GetJournalPath(tempDir);

                var journal = new PatchJournal
                {
                    Status = "InProgress",
                    Entries = new List<PatchJournalEntry>
                    {
                        new PatchJournalEntry { TargetPath = "bin/app.exe", BackupPath = "bin/app.exe.hina.bak" },
                        new PatchJournalEntry { TargetPath = "lib/core.dll", BackupPath = "lib/core.dll.hina.bak" }
                    }
                };

                await journal.SaveAsync(journalPath);
                Assert.True(File.Exists(journalPath));

                PatchJournal? loaded = PatchJournal.Load(journalPath);

                Assert.NotNull(loaded);
                Assert.Equal("InProgress", loaded!.Status);
                Assert.Equal(2, loaded.Entries.Count);
                Assert.Equal("bin/app.exe", loaded.Entries[0].TargetPath);
                Assert.Equal("bin/app.exe.hina.bak", loaded.Entries[0].BackupPath);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task SaveAsync_CreatesDirectory()
        {
            string tempDir = CreateTempDir();
            try
            {
                string journalPath = PatchJournal.GetJournalPath(tempDir);
                // .hina directory does not exist yet
                Assert.False(Directory.Exists(Path.GetDirectoryName(journalPath)));

                var journal = new PatchJournal();
                await journal.SaveAsync(journalPath);

                Assert.True(File.Exists(journalPath));
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void DefaultValues_AreSet()
        {
            var journal = new PatchJournal();

            Assert.Equal("InProgress", journal.Status);
            Assert.NotEqual(default, journal.CreatedUtc);
            Assert.Empty(journal.Entries);
        }

        [Fact]
        public async Task SaveAsync_Load_PreservesCompletedStatus()
        {
            string tempDir = CreateTempDir();
            try
            {
                string journalPath = PatchJournal.GetJournalPath(tempDir);
                var journal = new PatchJournal { Status = "Completed" };

                await journal.SaveAsync(journalPath);
                PatchJournal? loaded = PatchJournal.Load(journalPath);

                Assert.Equal("Completed", loaded!.Status);
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
