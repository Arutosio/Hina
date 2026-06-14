using Hina.Host;
using Xunit;

namespace Hina.Host.Tests
{
    // BUG-040: request path/User-Agent are logged; a percent-decoded newline in the path could
    // otherwise forge a log line under the default console formatter.
    public sealed class LogSanitizerTests
    {
        [Fact]
        public void SanitizeForLog_ReplacesControlChars_IncludingCrLf()
        {
            string forged = "/manifest\n2026-01-01 INFO forged-line.json";
            string clean = LogSanitizer.SanitizeForLog(forged);

            Assert.DoesNotContain('\n', clean);
            Assert.DoesNotContain('\r', clean);
            Assert.Contains('_', clean);
        }

        [Fact]
        public void SanitizeForLog_TruncatesToMax()
        {
            string huge = new string('a', 5000);
            Assert.Equal(256, LogSanitizer.SanitizeForLog(huge).Length);
        }

        [Fact]
        public void SanitizeForLog_PreservesOrdinaryText()
        {
            Assert.Equal("/demo/manifest.json", LogSanitizer.SanitizeForLog("/demo/manifest.json"));
        }

        [Fact]
        public void SanitizeForLog_NullOrEmpty_ReturnsEmpty()
        {
            Assert.Equal("", LogSanitizer.SanitizeForLog(null));
            Assert.Equal("", LogSanitizer.SanitizeForLog(""));
        }
    }
}
