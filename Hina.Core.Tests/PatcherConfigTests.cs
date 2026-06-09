using System;
using System.IO;
using System.Text.Json;
using Hina.Core.Configuration;
using Xunit;

namespace Hina.Core.Tests
{
    public class PatcherConfigTests
    {
        [Fact]
        public void PatcherConfig_DefaultValues()
        {
            var config = new PatcherConfig();

            Assert.Equal(new Uri("http://localhost/"), config.BaseUrl);
            Assert.Equal("stable", config.Channel);
            Assert.Equal(4, config.Concurrency);
            Assert.Equal(64 * 1024, config.ChunkSize);
            Assert.True(config.Verify);
            Assert.True(config.Backup);
            Assert.Null(config.TrustedPublicKey);
        }

        [Fact]
        public void PatcherConfigLoader_Load_ReadsJsonFile()
        {
            string tempDir = CreateTempDir();
            try
            {
                string configPath = Path.Combine(tempDir, "config.json");
                var json = new
                {
                    BaseUrl = "https://example.com/patch/",
                    Channel = "beta",
                    Concurrency = 8,
                    ChunkSize = 32768,
                    Verify = false,
                    Backup = false,
                    TrustedPublicKey = "abc123"
                };
                File.WriteAllText(configPath, JsonSerializer.Serialize(json));

                PatcherConfig config = PatcherConfigLoader.Load(configPath);

                Assert.Equal(new Uri("https://example.com/patch/"), config.BaseUrl);
                Assert.Equal("beta", config.Channel);
                Assert.Equal(8, config.Concurrency);
                Assert.Equal(32768, config.ChunkSize);
                Assert.False(config.Verify);
                Assert.False(config.Backup);
                Assert.Equal("abc123", config.TrustedPublicKey);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void PatcherConfigLoader_Load_CaseInsensitive()
        {
            string tempDir = CreateTempDir();
            try
            {
                string configPath = Path.Combine(tempDir, "config.json");
                // Use lowercase JSON keys
                File.WriteAllText(configPath, """{"baseUrl":"https://test.com/","channel":"nightly","concurrency":2}""");

                PatcherConfig config = PatcherConfigLoader.Load(configPath);

                Assert.Equal(new Uri("https://test.com/"), config.BaseUrl);
                Assert.Equal("nightly", config.Channel);
                Assert.Equal(2, config.Concurrency);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void PatcherConfigLoader_Load_EmptyJson_ReturnsDefaults()
        {
            string tempDir = CreateTempDir();
            try
            {
                string configPath = Path.Combine(tempDir, "config.json");
                File.WriteAllText(configPath, "{}");

                PatcherConfig config = PatcherConfigLoader.Load(configPath);

                Assert.Equal("stable", config.Channel);
                Assert.Equal(4, config.Concurrency);
                Assert.True(config.Verify);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void PatcherConfigLoader_Load_MissingFile_Throws()
        {
            Assert.ThrowsAny<Exception>(() => PatcherConfigLoader.Load("/nonexistent/path/config.json"));
        }

        // A hand-edited config with a typo must name the offending file — `hina dev` can be
        // reading either an explicit --config or an implicit ./hina.config.json, and a raw
        // JsonException doesn't say which one is broken.
        [Theory]
        [InlineData("{ truncated")]
        [InlineData("not json")]
        public void PatcherConfigLoader_Load_CorruptJson_ThrowsActionableError(string content)
        {
            string tempDir = CreateTempDir();
            try
            {
                string configPath = Path.Combine(tempDir, "hina.config.json");
                File.WriteAllText(configPath, content);

                var ex = Assert.Throws<InvalidDataException>(() => PatcherConfigLoader.Load(configPath));
                Assert.Contains(configPath, ex.Message);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        private static string CreateTempDir()
        {
            string path = Path.Combine(Path.GetTempPath(), "hina-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
