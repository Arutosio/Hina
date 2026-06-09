using Microsoft.Extensions.Configuration;

namespace Hina.Host.Tests
{
    public class HostOptionsTests : IDisposable
    {
        private readonly string _tempDir;

        public HostOptionsTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "hina_hostopts_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        }

        private static IConfiguration EmptyConfig() => new ConfigurationBuilder().Build();

        [Fact]
        public void Load_NoConfigNoArgs_UsesDefaults()
        {
            var opt = HostOptions.Load(Array.Empty<string>(), EmptyConfig());

            Assert.Equal("patch", opt.Root);
            Assert.Null(opt.Urls);
            Assert.Equal(600, opt.RequestsPerMinutePerIp);
            Assert.Equal(300, opt.AbuseThresholdPerMinute);
            Assert.True(opt.StatsEnabled);
            Assert.Empty(opt.Apps);
        }

        [Fact]
        public void Load_FromJsonFile_ReadsAllFields()
        {
            string cfg = Path.Combine(_tempDir, "hina.host.json");
            File.WriteAllText(cfg, """
                {
                  "root": "my-patches",
                  "urls": "http://127.0.0.1:5001",
                  "requestsPerMinutePerIp": 42,
                  "abuseThresholdPerMinute": 21,
                  "statsEnabled": false,
                  "cors": ["https://a.example", "https://b.example"],
                  "apps": { "gameA": "/srv/gameA" }
                }
                """);

            var opt = HostOptions.Load(new[] { "--config", cfg }, EmptyConfig());

            Assert.Equal("my-patches", opt.Root);
            Assert.Equal("http://127.0.0.1:5001", opt.Urls);
            Assert.Equal(42, opt.RequestsPerMinutePerIp);
            Assert.Equal(21, opt.AbuseThresholdPerMinute);
            Assert.False(opt.StatsEnabled);
            Assert.Equal(2, opt.Cors.Count);
            Assert.Equal("/srv/gameA", opt.Apps["gameA"]);
        }

        [Fact]
        public void Load_CliFlags_OverrideJson()
        {
            string cfg = Path.Combine(_tempDir, "hina.host.json");
            File.WriteAllText(cfg, """{ "root": "from-json", "requestsPerMinutePerIp": 42 }""");

            var opt = HostOptions.Load(new[]
            {
                "--config", cfg,
                "--root", "from-cli",
                "--port", "1234",
                "--rate-limit", "7",
                "--no-stats"
            }, EmptyConfig());

            Assert.Equal("from-cli", opt.Root);
            Assert.Equal("http://0.0.0.0:1234", opt.Urls);
            Assert.Equal(7, opt.RequestsPerMinutePerIp);
            Assert.False(opt.StatsEnabled);
        }

        [Fact]
        public void Load_RateLimitZero_DisablesLimit()
        {
            var opt = HostOptions.Load(new[] { "--rate-limit", "0" }, EmptyConfig());
            Assert.Equal(int.MaxValue, opt.RequestsPerMinutePerIp);
        }

        [Fact]
        public void Load_LegacyPatcherRootSetting_OverridesDefault()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Patcher:Root"] = "legacy-root" })
                .Build();

            var opt = HostOptions.Load(Array.Empty<string>(), config);
            Assert.Equal("legacy-root", opt.Root);
        }
    }
}
