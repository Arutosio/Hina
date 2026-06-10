using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Hina.Core.Json;

namespace Hina.Core.Patching
{
    // Tracks backups for rollback across patch sessions.
    public sealed class PatchJournal
    {
        public string Status { get; set; } = "InProgress";
        public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
        public List<PatchJournalEntry> Entries { get; set; } = new List<PatchJournalEntry>();

        public static string GetJournalPath(string rootDir)
        {
            return Path.Combine(rootDir, ".hina", "journal.json");
        }

        public static PatchJournal? Load(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }
            try
            {
                string json = File.ReadAllText(path);
                PatchJournal? journal = JsonSerializer.Deserialize(json, HinaCoreJsonContext.Default.PatchJournal);
                if (journal == null)
                {
                    throw new JsonException("Journal deserialized to null.");
                }
                return journal;
            }
            catch (JsonException ex)
            {
                // A corrupt journal (interrupted save, full disk) would otherwise make every
                // future patch fail with a raw parser error and no recovery hint — and the
                // user has no way to know the journal file is the blocker.
                throw new InvalidDataException(
                    $"Patch journal at '{path}' is corrupt (likely an interrupted save). " +
                    "Backups (*.hina.bak) may still exist next to the patched files; restore them " +
                    "manually if needed, then delete the journal file to let patching continue.", ex);
            }
        }

        public async Task SaveAsync(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            using (FileStream fs = File.Create(path))
            {
                await JsonSerializer.SerializeAsync(fs, this, HinaCoreIndentedJsonContext.Default.PatchJournal);
            }
        }
    }

    public sealed class PatchJournalEntry
    {
        public string TargetPath { get; set; } = string.Empty;
        public string BackupPath { get; set; } = string.Empty;
        // A net-new file (no prior version to back up). Rollback removes it rather than restoring,
        // so a multi-file patch that fails partway can't leave new files behind next to rolled-back
        // existing ones (mixed-version state).
        public bool IsNew { get; set; }
    }
}
