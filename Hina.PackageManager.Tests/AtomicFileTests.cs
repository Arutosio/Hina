using System.IO;
using Hina.PackageManager.Io;
using Xunit;

namespace Hina.PackageManager.Tests
{
    public class AtomicFileTests
    {
        [Fact]
        public void WriteAllText_WritesContent_AndLeavesNoTmp()
        {
            string dir = Path.Combine(Path.GetTempPath(), "hina-atomic-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try
            {
                string target = Path.Combine(dir, "sub", "file.json");
                AtomicFile.WriteAllText(target, "{\"a\":1}");

                Assert.Equal("{\"a\":1}", File.ReadAllText(target));
                Assert.False(File.Exists(target + ".tmp"), "temp file must be renamed away");
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void WriteAllText_OverwritesExisting()
        {
            string dir = Path.Combine(Path.GetTempPath(), "hina-atomic-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try
            {
                string target = Path.Combine(dir, "file.txt");
                File.WriteAllText(target, "old");
                AtomicFile.WriteAllText(target, "new");
                Assert.Equal("new", File.ReadAllText(target));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }
    }
}
