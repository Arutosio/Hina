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
        public void Load_CorruptJsonConfig_ThrowsActionableError()
        {
            // A service/container start with a corrupt hina.host.json must produce an error
            // naming the file, not an unhandled JsonReaderException stack trace (exit 134).
            string cfg = Path.Combine(_tempDir, "hina.host.json");
            File.WriteAllText(cfg, "{ \"root\": \"patch\", brokenjson");

            var ex = Assert.Throws<InvalidDataException>(
                () => HostOptions.Load(new[] { "--config", cfg }, EmptyConfig()));

            Assert.Contains(cfg, ex.Message);
        }

        [Fact]
        public void Load_PortOutOfRange_ThrowsActionableError()
        {
            // `--port 99999` used to flow into Kestrel and crash at bind time with a raw
            // ArgumentOutOfRangeException. The wizard validates ports; the CLI flag must too.
            var ex = Assert.Throws<InvalidDataException>(
                () => HostOptions.Load(new[] { "--port", "99999" }, EmptyConfig()));

            Assert.Contains("--port", ex.Message);
            Assert.Contains("99999", ex.Message);
        }

        [Fact]
        public void Load_PortNotANumber_ThrowsActionableError()
        {
            // A typo'd `--port abc` was silently ignored and the host bound the default port —
            // the user thinks they chose a port and the server is listening elsewhere.
            var ex = Assert.Throws<InvalidDataException>(
                () => HostOptions.Load(new[] { "--port", "abc" }, EmptyConfig()));

            Assert.Contains("--port", ex.Message);
        }

        [Theory]
        [InlineData("")]
        [InlineData("game/a")]
        [InlineData("   ")]
        public void Load_InvalidAppName_ThrowsActionableError(string appName)
        {
            // An empty app name mounts at "/" but the unknown-prefix filter then 404s every
            // request; a name with '/' can never match a first path segment. Both leave the
            // server up but serving nothing — fail fast at load instead.
            string cfg = Path.Combine(_tempDir, "hina.host.json");
            File.WriteAllText(cfg, $$"""{ "apps": { {{System.Text.Json.JsonSerializer.Serialize(appName)}}: "/srv/x" } }""");

            var ex = Assert.Throws<InvalidDataException>(
                () => HostOptions.Load(new[] { "--config", cfg }, EmptyConfig()));

            Assert.Contains("apps", ex.Message);
        }

        [Theory]
        [InlineData("-5")]
        [InlineData("abc")]
        public void Load_InvalidRateLimit_ThrowsActionableError(string value)
        {
            // A negative rate limit boots the server fine but every request then 500s
            // (FixedWindowRateLimiterOptions rejects negative PermitLimit per partition);
            // a non-numeric value was silently ignored. Both must fail at startup.
            var ex = Assert.Throws<InvalidDataException>(
                () => HostOptions.Load(new[] { "--rate-limit", value }, EmptyConfig()));

            Assert.Contains("--rate-limit", ex.Message);
        }

        [Fact]
        public void Load_NegativeRateLimitInJson_ThrowsActionableError()
        {
            string cfg = Path.Combine(_tempDir, "hina.host.json");
            File.WriteAllText(cfg, """{ "requestsPerMinutePerIp": -5 }""");

            var ex = Assert.Throws<InvalidDataException>(
                () => HostOptions.Load(new[] { "--config", cfg }, EmptyConfig()));

            Assert.Contains("requestsPerMinutePerIp", ex.Message);
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

        [Fact]
        public void Load_RootIsNotAnObject_ThrowsActionableError()
        {
            // A config whose root is an array/string parses as JSON, but TryGetProperty
            // on a non-object root throws a raw InvalidOperationException at startup.
            string cfg = Path.Combine(_tempDir, "hina.host.json");
            File.WriteAllText(cfg, "[1,2,3]");

            var ex = Assert.Throws<InvalidDataException>(
                () => HostOptions.Load(new[] { "--config", cfg }, EmptyConfig()));

            Assert.Contains(cfg, ex.Message);
            Assert.Contains("object", ex.Message);
        }

        [Fact]
        public void Load_TrustedProxies_DefaultsToEmpty()
        {
            var opt = HostOptions.Load(Array.Empty<string>(), EmptyConfig());
            Assert.Empty(opt.TrustedProxies);
        }

        [Fact]
        public void Load_TrustedProxiesFromJson_ParsesList()
        {
            string cfg = Path.Combine(_tempDir, "hina.host.json");
            File.WriteAllText(cfg, """{ "trustedProxies": ["10.1.2.3", "2001:db8::1"] }""");

            var opt = HostOptions.Load(new[] { "--config", cfg }, EmptyConfig());

            Assert.Equal(new[] { "10.1.2.3", "2001:db8::1" }, opt.TrustedProxies);
        }

        [Fact]
        public void Load_TrustedProxiesFromFlag_ParsesCommaSeparatedList()
        {
            var opt = HostOptions.Load(new[] { "--trusted-proxies", "10.1.2.3, 10.1.2.4" }, EmptyConfig());

            Assert.Equal(new[] { "10.1.2.3", "10.1.2.4" }, opt.TrustedProxies);
        }

        [Theory]
        [InlineData("not-an-ip")]
        [InlineData("10.1.2.3/24")]
        [InlineData("proxy.example.com")]
        public void Load_InvalidTrustedProxyInJson_ThrowsActionableError(string value)
        {
            // CIDR ranges and hostnames are not supported — only literal IPs. A typo'd
            // entry must fail at startup, not silently leave the proxy untrusted (which
            // would put every client in one rate-limit bucket with no visible cause).
            string cfg = Path.Combine(_tempDir, "hina.host.json");
            File.WriteAllText(cfg, $$"""{ "trustedProxies": ["{{value}}"] }""");

            var ex = Assert.Throws<InvalidDataException>(
                () => HostOptions.Load(new[] { "--config", cfg }, EmptyConfig()));

            Assert.Contains("trustedProxies", ex.Message);
            Assert.Contains(value, ex.Message);
        }

        [Fact]
        public void Load_InvalidTrustedProxyInFlag_ThrowsActionableError()
        {
            var ex = Assert.Throws<InvalidDataException>(
                () => HostOptions.Load(new[] { "--trusted-proxies", "nope" }, EmptyConfig()));

            Assert.Contains("--trusted-proxies", ex.Message);
            Assert.Contains("nope", ex.Message);
        }
    }
}
