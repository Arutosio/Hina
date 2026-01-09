using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

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
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PatchJournal>(json);
        }

        public async Task SaveAsync(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            using (FileStream fs = File.Create(path))
            {
                await JsonSerializer.SerializeAsync(fs, this, new JsonSerializerOptions { WriteIndented = true });
            }
        }
    }

    public sealed class PatchJournalEntry
    {
        public string TargetPath { get; set; } = string.Empty;
        public string BackupPath { get; set; } = string.Empty;
    }
}
