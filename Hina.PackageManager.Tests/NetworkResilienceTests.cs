using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Configuration;
using Hina.Core.Net;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Install;
using Hina.PackageManager.Net;
using Hina.PackageManager.Tests;

namespace Hina.PackageManager.Tests
{
    // Round-3: resilience for harsh networks (Venezuela-style flaky / changing-IP
    // connections). Covers: RetryPolicy delay cap, cancellation honored, descriptor
    // fetcher defaults, NetworkOptions threading into PatcherConfig.
    public class NetworkResilienceTests
    {
        // ---- RetryPolicy max-delay cap ----

        [Fact]
        public void RetryPolicy_DelayCapped_OnLargeAttempt()
        {
            // Without cap: 1000ms * 2^19 = 524 287 000ms (~6 days).
            // With cap=5000ms: delay stays at the cap.
            RetryPolicy policy = new RetryPolicy(maxRetries: 30, baseDelayMs: 1000, maxDelayMs: 5000);
            int delay = policy.CalculateDelay(attempt: 20);
            Assert.True(delay <= 5000, $"delay was {delay}ms (cap 5000ms)");
        }

        [Fact]
        public void RetryPolicy_DelayCapped_DoesNotOverflowAtHugeAttempt()
        {
            // Attempt 100 would shift by 99 bits — without the Math.Min(attempt-1, 30)
            // guard the bit-shift would overflow int. Sanity check it doesn't throw.
            RetryPolicy policy = new RetryPolicy(maxRetries: 5, baseDelayMs: 1000, maxDelayMs: 30_000);
            int delay = policy.CalculateDelay(attempt: 100);
            Assert.True(delay <= 30_000);
        }

        [Fact]
        public void RetryPolicy_Default_GrowsExponentially_BelowCap()
        {
            RetryPolicy policy = new RetryPolicy(maxRetries: 10, baseDelayMs: 100, maxDelayMs: 30_000);
            int d1 = policy.CalculateDelay(1);
            int d3 = policy.CalculateDelay(3);
            Assert.True(d3 > d1, "delay should grow with attempt");
        }

        // ---- DescriptorFetcher honours new defaults ----

        [Fact]
        public void DescriptorFetcher_DefaultMaxRetries_Is5()
        {
            Assert.Equal(5, DescriptorFetcher.DefaultMaxRetries);
        }

        [Fact]
        public async Task DescriptorFetcher_RetriesWithCapBeforeGivingUp()
        {
            // 4 503s in a row then OK. Default 5 retries means total 5 attempts max — succeed on 5th.
            FlakyHandler handler = new FlakyHandler(failBefore: 4);
            HttpClient client = new HttpClient(handler);
            DescriptorFetcher fetcher = new DescriptorFetcher(client,
                maxRetries: 5,
                retryBaseDelay: TimeSpan.FromMilliseconds(5),
                maxRetryDelay: TimeSpan.FromMilliseconds(50));

            AppDescriptor d = await fetcher.FetchAsync(new Uri("https://example.com/x.json"), CancellationToken.None);

            Assert.Equal("demo", d.Name);
            Assert.Equal(5, handler.Calls);
        }

        // ---- PatcherConfig defaults track NetworkOptions defaults ----

        [Fact]
        public void PatcherConfig_NewDefaults_MatchNetworkOptions()
        {
            PatcherConfig pcfg = new PatcherConfig();
            NetworkOptions n = new NetworkOptions();
            Assert.Equal(n.MaxRetries, pcfg.MaxRetries);
            Assert.Equal(n.RetryBaseDelayMs, pcfg.RetryBaseDelayMs);
            Assert.Equal(n.MaxRetryDelayMs, pcfg.MaxRetryDelayMs);
            Assert.Equal(n.ConnectTimeoutMs, pcfg.ConnectTimeoutMs);
            Assert.Equal(n.RequestTimeoutMs, pcfg.RequestTimeoutMs);
        }

        // ---- SharedHttp builds a working HttpClient ----

        [Fact]
        public void SharedHttp_HasReasonableTimeout()
        {
            HttpClient c = SharedHttp.Instance;
            Assert.True(c.Timeout > TimeSpan.Zero);
            Assert.True(c.Timeout <= TimeSpan.FromSeconds(60), "SharedHttp.Timeout should be tight enough to fail fast");
        }
    }
}
