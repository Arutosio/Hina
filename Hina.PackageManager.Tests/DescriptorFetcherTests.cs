using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Descriptor;
using Xunit;

namespace Hina.PackageManager.Tests
{
    // B1: the descriptor fetch must refuse an HTTPS→HTTP downgrade on redirect, since the
    // first-install (TOFU) key would otherwise arrive over a tamperable channel.
    public class DescriptorFetcherTests
    {
        private const string MinimalDescriptor =
            "{\"name\":\"demo\",\"version\":\"1.0.0\",\"baseUrl\":\"https://cdn.test/\",\"publicKey\":\"x\"}";

        [Fact]
        public async Task FetchAsync_HttpsRedirectedToHttp_Throws()
        {
            // Simulate the post-redirect state: request started https, landed on http.
            var handler = new FakeHandler((req, ct) =>
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(MinimalDescriptor, Encoding.UTF8, "application/json"),
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://cdn.test/hina.app.json")
                };
                return Task.FromResult(resp);
            });

            var fetcher = new DescriptorFetcher(new HttpClient(handler), maxRetries: 1);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fetcher.FetchAsync(new Uri("https://cdn.test/hina.app.json"), CancellationToken.None));
            Assert.Contains("downgrade", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task FetchAsync_HttpsStaysHttps_Succeeds()
        {
            var handler = new FakeHandler((req, ct) =>
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(MinimalDescriptor, Encoding.UTF8, "application/json"),
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://cdn.test/hina.app.json")
                };
                return Task.FromResult(resp);
            });

            var fetcher = new DescriptorFetcher(new HttpClient(handler), maxRetries: 1);

            AppDescriptor d = await fetcher.FetchAsync(new Uri("https://cdn.test/hina.app.json"), CancellationToken.None);
            Assert.Equal("demo", d.Name);
        }

        [Fact]
        public async Task FetchAsync_HttpStaysHttp_Succeeds()
        {
            // Starting on http (only reachable via --allow-insecure upstream) is not a downgrade.
            var handler = new FakeHandler((req, ct) =>
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(MinimalDescriptor, Encoding.UTF8, "application/json"),
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://cdn.test/hina.app.json")
                };
                return Task.FromResult(resp);
            });

            var fetcher = new DescriptorFetcher(new HttpClient(handler), maxRetries: 1);

            AppDescriptor d = await fetcher.FetchAsync(new Uri("http://cdn.test/hina.app.json"), CancellationToken.None);
            Assert.Equal("demo", d.Name);
        }

        private sealed class FakeHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
            public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => _handler = handler;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => _handler(request, cancellationToken);
        }
    }
}
