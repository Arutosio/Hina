using System.IO;

namespace Hina.Core.Patching
{
    // Cleans leftover temp/backup files and the patch journal.
    public static class PatchCleanup
    {
        public static void Cleanup(string rootDir)
        {
            if (!Directory.Exists(rootDir))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(rootDir, "*.*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".hina.tmp") || file.EndsWith(".hina.bak"))
                {
                    File.Delete(file);
                }
            }

            string journalPath = PatchJournal.GetJournalPath(rootDir);
            if (File.Exists(journalPath))
            {
                File.Delete(journalPath);
            }
        }
    }
}
