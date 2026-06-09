namespace Hina.Host.Tests
{
    public class AccessStatsTests
    {
        [Fact]
        public void RecordRequest_CountsTotalsAndTops()
        {
            var stats = new AccessStats();
            stats.RecordRequest("1.2.3.4", "gameA", "/gameA/manifest.json");
            stats.RecordRequest("1.2.3.4", "gameA", "/gameA/manifest.json");
            stats.RecordRequest("5.6.7.8", "gameB", "/gameB/chunks/ab/x.chunk.br");

            var snap = stats.Snapshot();

            Assert.Equal(3, snap.TotalRequests);
            Assert.Equal(0, snap.Rejections);
            Assert.Equal("1.2.3.4", snap.TopIp);
            Assert.Equal("gameA", snap.TopApp);
            Assert.Equal("/gameA/manifest.json", snap.TopPath);
        }

        [Fact]
        public void RecordRejection_CountsPerApp()
        {
            var stats = new AccessStats();
            stats.RecordRejection("1.2.3.4", "gameA");
            stats.RecordRejection("1.2.3.4", "gameA");

            var snap = stats.Snapshot();

            Assert.Equal(2, snap.Rejections);
            Assert.Equal(2, snap.AppRejections["gameA"]);
        }

        [Fact]
        public void ShouldLogAbuse_BelowThreshold_False()
        {
            var stats = new AccessStats();
            stats.RecordRequest("1.2.3.4", "gameA", "/x");

            Assert.False(stats.ShouldLogAbuse("1.2.3.4", "gameA", threshold: 5, out long count));
            Assert.Equal(1, count);
        }

        [Fact]
        public void ShouldLogAbuse_AtThreshold_TrueOnceThenSuppressed()
        {
            var stats = new AccessStats();
            for (int i = 0; i < 5; i++) stats.RecordRequest("1.2.3.4", "gameA", "/x");

            // First crossing logs; a second check inside the same minute is rate-limited.
            Assert.True(stats.ShouldLogAbuse("1.2.3.4", "gameA", threshold: 5, out _));
            Assert.False(stats.ShouldLogAbuse("1.2.3.4", "gameA", threshold: 5, out _));
        }

        [Fact]
        public void ShouldLogAbuse_UnknownKey_False()
        {
            var stats = new AccessStats();
            Assert.False(stats.ShouldLogAbuse("9.9.9.9", "nope", threshold: 1, out long count));
            Assert.Equal(0, count);
        }
    }
}
