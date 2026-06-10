namespace Hina.Host.Tests
{
    // A public host gets scanned: every probe brings a fresh path and many bring fresh IPs.
    // The per-path and per-(ip|app) tracking tables must stop growing at a cap instead of
    // accumulating for the lifetime of the process (slow memory exhaustion).
    public class AccessStatsBoundsTests
    {
        [Fact]
        public void RecordRequest_UniquePathFlood_PathTableIsBounded()
        {
            var stats = new AccessStats();
            for (int i = 0; i < AccessStats.MaxTrackedPaths + 500; i++)
            {
                stats.RecordRequest("1.2.3.4", "default", $"/probe/{i}");
            }

            Assert.True(stats.TrackedPathCount <= AccessStats.MaxTrackedPaths,
                $"paths grew to {stats.TrackedPathCount}");
            // The total counter must keep counting even past the cap.
            Assert.Equal(AccessStats.MaxTrackedPaths + 500, stats.Snapshot().TotalRequests);
        }

        [Fact]
        public void RecordRequest_UniqueIpFlood_KeyTableIsBounded()
        {
            var stats = new AccessStats();
            for (int i = 0; i < AccessStats.MaxTrackedKeys + 500; i++)
            {
                stats.RecordRequest($"10.0.{i / 256}.{i % 256}-{i}", "default", "/manifest.json");
            }

            Assert.True(stats.TrackedKeyCount <= AccessStats.MaxTrackedKeys,
                $"keys grew to {stats.TrackedKeyCount}");
        }

        [Fact]
        public void RecordRequest_KnownPathPastCap_StillCounted()
        {
            var stats = new AccessStats();
            stats.RecordRequest("1.2.3.4", "default", "/manifest.json");
            for (int i = 0; i < AccessStats.MaxTrackedPaths + 10; i++)
            {
                stats.RecordRequest("1.2.3.4", "default", $"/probe/{i}");
            }

            // A path tracked before the cap keeps updating after it.
            stats.RecordRequest("1.2.3.4", "default", "/manifest.json");
            Assert.Equal("/manifest.json", stats.Snapshot().TopPath);
        }
    }
}
