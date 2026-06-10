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
        public void IsConfigMissingOrEmpty_WhitespaceOnly_True()
        {
            string p = PathFor("blank.json");
            File.WriteAllText(p, "  \n\t ");
            Assert.True(SetupWizard.IsConfigMissingOrEmpty(p));
        }

        // A corrupt-but-non-empty config must NOT trigger the auto-wizard: the wizard
        // ends with File.WriteAllText over the user's file, so a single JSON typo in a
        // hand-maintained config (apps, cors, ...) would destroy it after the prompts.
        // Corrupt configs flow to HostOptions.Load, which names the file and the error.
        [Fact]
        public void IsConfigMissingOrEmpty_NotAnObject_False()
        {
            string p = PathFor("array.json");
            File.WriteAllText(p, "[1,2,3]");
            Assert.False(SetupWizard.IsConfigMissingOrEmpty(p));
        }

        [Fact]
        public void IsConfigMissingOrEmpty_InvalidJson_False()
        {
            string p = PathFor("broken.json");
            File.WriteAllText(p, """{ "urls": "http://0.0.0.0:5000" "apps": {} }""");
            Assert.False(SetupWizard.IsConfigMissingOrEmpty(p));
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
