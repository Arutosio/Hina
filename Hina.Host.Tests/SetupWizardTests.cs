namespace Hina.Host.Tests
{
    public class SetupWizardTests : IDisposable
    {
        private readonly string _tempDir;

        public SetupWizardTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "hina_wizard_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        }

        private string PathFor(string name) => Path.Combine(_tempDir, name);

        [Fact]
        public void IsConfigMissingOrEmpty_MissingFile_True()
        {
            Assert.True(SetupWizard.IsConfigMissingOrEmpty(PathFor("absent.json")));
        }

        [Fact]
        public void IsConfigMissingOrEmpty_EmptyObject_True()
        {
            string p = PathFor("empty.json");
            File.WriteAllText(p, "{}");
            Assert.True(SetupWizard.IsConfigMissingOrEmpty(p));
        }

        [Fact]
        public void IsConfigMissingOrEmpty_NotAnObject_True()
        {
            string p = PathFor("array.json");
            File.WriteAllText(p, "[1,2,3]");
            Assert.True(SetupWizard.IsConfigMissingOrEmpty(p));
        }

        [Fact]
        public void IsConfigMissingOrEmpty_InvalidJson_True()
        {
            string p = PathFor("broken.json");
            File.WriteAllText(p, "not json at all");
            Assert.True(SetupWizard.IsConfigMissingOrEmpty(p));
        }

        [Fact]
        public void IsConfigMissingOrEmpty_PopulatedConfig_False()
        {
            string p = PathFor("ok.json");
            File.WriteAllText(p, """{ "urls": "http://127.0.0.1:5000" }""");
            Assert.False(SetupWizard.IsConfigMissingOrEmpty(p));
        }
    }
}
