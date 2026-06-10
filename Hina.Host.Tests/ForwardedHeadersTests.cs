using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Hina.Host.Tests
{
    // The rate limiter and the /stats loopback gate both key on Connection.RemoteIpAddress,
    // which UseForwardedHeaders rewrites from X-Forwarded-For only for trusted proxies.
    // Behind a remote reverse proxy / load balancer every client used to share the proxy's
    // IP (one rate-limit bucket for everyone); "trustedProxies" opts that proxy in.
    //
    // /stats (loopback-only) is the observable: if X-Forwarded-For: 127.0.0.1 from a given
    // remote address flips /stats to 200, the forwarded client IP was trusted.
    public class ForwardedHeadersTests : IDisposable
    {
        private readonly string _root;

        public ForwardedHeadersTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "hina_host_fwd_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }

        // Prepends a middleware that fakes the TestServer connection's remote address,
        // simulating a connection arriving from a given proxy/client IP. IStartupFilter
        // middlewares run before the app pipeline, so UseForwardedHeaders sees the fake.
        private sealed class FakeRemoteIpFilter : IStartupFilter
        {
            private readonly IPAddress _ip;
            public FakeRemoteIpFilter(IPAddress ip) => _ip = ip;

            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
                app =>
                {
                    app.Use(async (ctx, nxt) =>
                    {
                        ctx.Connection.RemoteIpAddress = _ip;
                        await nxt();
                    });
                    next(app);
                };
        }

        private WebApplicationFactory<Program> Boot(string remoteIp, string? trustedProxies)
        {
            return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseSetting("Patcher:Root", _root);
                if (trustedProxies is not null)
                    b.UseSetting("TrustedProxies", trustedProxies);
                b.ConfigureServices(s =>
                    s.AddSingleton<IStartupFilter>(new FakeRemoteIpFilter(IPAddress.Parse(remoteIp))));
            });
        }

        [Fact]
        public async Task TrustedProxy_ForwardedClientIp_IsHonored()
        {
            using var factory = Boot("10.1.2.3", trustedProxies: "10.1.2.3");
            using var client = factory.CreateClient();

            var req = new HttpRequestMessage(HttpMethod.Get, "/stats");
            req.Headers.Add("X-Forwarded-For", "127.0.0.1");
            var resp = await client.SendAsync(req);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        [Fact]
        public async Task UntrustedRemote_ForwardedClientIp_IsIgnored()
        {
            // 10.9.9.9 is not in the trusted list: its X-Forwarded-For must not be believed.
            using var factory = Boot("10.9.9.9", trustedProxies: "10.1.2.3");
            using var client = factory.CreateClient();

            var req = new HttpRequestMessage(HttpMethod.Get, "/stats");
            req.Headers.Add("X-Forwarded-For", "127.0.0.1");
            var resp = await client.SendAsync(req);

            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }

        [Fact]
        public async Task NoTrustedProxies_RemoteForwardedClientIp_IsIgnored()
        {
            // Default deployment (no trustedProxies): a remote caller spoofing
            // X-Forwarded-For: 127.0.0.1 must not reach the loopback-only /stats.
            using var factory = Boot("10.1.2.3", trustedProxies: null);
            using var client = factory.CreateClient();

            var req = new HttpRequestMessage(HttpMethod.Get, "/stats");
            req.Headers.Add("X-Forwarded-For", "127.0.0.1");
            var resp = await client.SendAsync(req);

            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
    }
}
