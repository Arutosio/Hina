using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager;
using Hina.PackageManager.Install;

namespace Hina.PackageManager.Tests
{
    public class SelfUpdateTests
    {
        [Fact]
        public void ParseTagName_ExtractsTag()
        {
            string json = "{\"url\":\"x\",\"tag_name\": \"v1.2.3\",\"name\":\"Hina v1.2.3\"}";
            Assert.Equal("v1.2.3", SelfUpdate.ParseTagName(json));
        }

        [Fact]
        public void ParseTagName_MissingTag_ReturnsNull()
        {
            Assert.Null(SelfUpdate.ParseTagName("{\"name\":\"no tag here\"}"));
        }

        [Theory]
        [InlineData("1.0.0", "v1.0.0", UpdateAvailability.UpToDate)]
        [InlineData("1.0.0", "v1.0.1", UpdateAvailability.UpdateAvailable)]
        [InlineData("1.0.0", "v0.9.9", UpdateAvailability.UpToDate)]   // running ahead of latest release
        [InlineData("1.2.0", "1.2.0", UpdateAvailability.UpToDate)]    // tag without leading v
        [InlineData("1.0.0", "v2.0.0", UpdateAvailability.UpdateAvailable)]
        public void Evaluate_ComparesSemVer(string current, string latestTag, UpdateAvailability expected)
        {
            UpdateCheckResult r = SelfUpdate.Evaluate(current, latestTag);
            Assert.Equal(expected, r.Status);
        }

        [Fact]
        public void Evaluate_NullOrGarbageTag_IsUnknown()
        {
            Assert.Equal(UpdateAvailability.Unknown, SelfUpdate.Evaluate("1.0.0", null).Status);
            Assert.Equal(UpdateAvailability.Unknown, SelfUpdate.Evaluate("1.0.0", "not-a-version").Status);
        }

        [Fact]
        public async Task CheckAsync_FetchThrows_IsUnknownNotCrash()
        {
            UpdateCheckResult r = await SelfUpdate.CheckAsync(
                "1.0.0",
                _ => throw new System.Net.Http.HttpRequestException("offline"),
                CancellationToken.None);

            Assert.Equal(UpdateAvailability.Unknown, r.Status);
            Assert.Contains("offline", r.Error);
        }

        [Fact]
        public async Task CheckAsync_RealPayload_DetectsUpdate()
        {
            UpdateCheckResult r = await SelfUpdate.CheckAsync(
                "1.0.0",
                _ => Task.FromResult("{\"tag_name\":\"v1.4.0\"}"),
                CancellationToken.None);

            Assert.Equal(UpdateAvailability.UpdateAvailable, r.Status);
            Assert.Equal("1.4.0", r.LatestVersion);
        }

        [Theory]
        [InlineData("1.0.0", "1.0.0", 0)]
        [InlineData("1.0.0", "1.0.1", -1)]
        [InlineData("1.1.0", "1.0.9", 1)]
        [InlineData("2.0.0", "1.9.9", 1)]
        public void HinaVersion_TryCompare(string a, string b, int expectedSign)
        {
            Assert.True(HinaVersion.TryCompare(a, b, out int sign));
            Assert.Equal(expectedSign, sign);
        }

        [Fact]
        public void HinaVersion_TryCompare_Unparseable_ReturnsFalse()
        {
            Assert.False(HinaVersion.TryCompare("1.0", "1.0.0", out _));
        }
    }
}
