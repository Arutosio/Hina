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

        // A port typo accepted by the wizard gets persisted into hina.host.json and then
        // crashes the host on every start — and the wizard won't re-run because the config
        // is non-empty. The wizard must reject invalid ports up front.
        [Theory]
        [InlineData("49876", true)]
        [InlineData("1", true)]
        [InlineData("65535", true)]
        [InlineData("0", false)]
        [InlineData("65536", false)]
        [InlineData("-1", false)]
        [InlineData("8o80", false)]
        [InlineData("", false)]
        [InlineData("8080 ", true)]
        public void IsValidPort_Cases(string value, bool expected)
        {
            Assert.Equal(expected, SetupWizard.IsValidPort(value));
        }

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
