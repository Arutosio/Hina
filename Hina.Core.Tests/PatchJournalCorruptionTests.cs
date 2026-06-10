using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Configuration;
using Hina.Core.Patching;
using Xunit;

namespace Hina.Core.Tests
{
    // A corrupt journal (interrupted save, disk full, manual edit) must surface an
    // actionable error naming the journal file — not a raw JsonException. Without this,
    // every subsequent patch fails forever with a cryptic parser message and the user
    // has no way to know that deleting .hina/journal.json is the way out.
    public class PatchJournalCorruptionTests : IDisposable
    {
        private readonly string _tempRoot;

        public PatchJournalCorruptionTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "hina_journal_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best effort */ }
        }

        private string WriteCorruptJournal(string content)
        {
            string path = PatchJournal.GetJournalPath(_tempRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        [Theory]
        [InlineData("{ truncated")]
        [InlineData("not json at all")]
        [InlineData("")]
        [InlineData("null")]
        public void Load_CorruptJournal_ThrowsActionableError(string content)
        {
            string path = WriteCorruptJournal(content);

            var ex = Assert.Throws<InvalidDataException>(() => PatchJournal.Load(path));

            // The message must name the file and hint at recovery, so the user can act.
            Assert.Contains(path, ex.Message);
            Assert.Contains("corrupt", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Load_MissingJournal_ReturnsNull()
        {
            Assert.Null(PatchJournal.Load(PatchJournal.GetJournalPath(_tempRoot)));
        }

        [Fact]
        public async Task Load_ValidJournal_RoundTrips()
        {
            var journal = new PatchJournal();
            journal.Entries.Add(new PatchJournalEntry { TargetPath = "a", BackupPath = "b" });
            string path = PatchJournal.GetJournalPath(_tempRoot);
            await journal.SaveAsync(path);

            PatchJournal? loaded = PatchJournal.Load(path);

            Assert.NotNull(loaded);
            Assert.Single(loaded!.Entries);
        }

        [Fact]
        public async Task RollbackAsync_CorruptJournal_ThrowsActionableError()
        {
            string path = WriteCorruptJournal("{ broken");
            var client = new PatchClient(new PatcherConfig
            {
                BaseUrl = new Uri("http://test.local/"),
                Channel = "stable"
            });

            var ex = await Assert.ThrowsAsync<InvalidDataException>(
                () => client.RollbackAsync(_tempRoot, CancellationToken.None));
            Assert.Contains(path, ex.Message);
        }
    }
}
