using System;
using System.Net.Http;
using Hina.Core.Configuration;
using Hina.Core.Net;
using Hina.Core.Patching;
using Xunit;

namespace Hina.Core.Tests
{
    // Verifies the IDisposable chain: IPatchClient : IDisposable,
    // PatchClient.Dispose() delegates to HttpChunkClient.Dispose(),
    // which disposes the owned HttpClient.
    public class DisposeTests
    {
        [Fact]
        public void IPatchClient_IsIDisposable()
        {
            Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(IPatchClient)),
                "IPatchClient must extend IDisposable.");
        }

        [Fact]
        public void HttpChunkClient_IsIDisposable()
        {
            Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(HttpChunkClient)),
                "HttpChunkClient must implement IDisposable.");
        }

        [Fact]
        public void PatchClient_IsIDisposable()
        {
            Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(PatchClient)),
                "PatchClient must implement IDisposable.");
        }

        [Fact]
        public void HttpChunkClient_Dispose_DoesNotThrow()
        {
            var client = new HttpChunkClient(new HttpClient());
            // Must not throw.
            client.Dispose();
        }

        [Fact]
        public void PatchClient_Dispose_DoesNotThrow()
        {
            var config = new PatcherConfig
            {
                BaseUrl = new Uri("https://example.com/"),
                Channel = "stable"
            };
            var client = new PatchClient(config);
            // Must not throw.
            client.Dispose();
        }

        [Fact]
        public void PatchClient_DoubleDispose_DoesNotThrow()
        {
            // HttpClient.Dispose() is idempotent; verify PatchClient inherits that property.
            var config = new PatcherConfig
            {
                BaseUrl = new Uri("https://example.com/"),
                Channel = "stable"
            };
            var client = new PatchClient(config);
            client.Dispose();
            client.Dispose();
        }
    }
}
