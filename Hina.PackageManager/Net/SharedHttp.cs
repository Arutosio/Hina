using System;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Hina.PackageManager.Net
{
    // Process-wide HttpClient singleton. HttpClient is designed to be long-lived and
    // shared — creating a new one per service exhausts ephemeral sockets under load
    // (the classic "DotNet HttpClient socket exhaustion" bug). DescriptorFetcher and
    // any other Hina.PackageManager component that does HTTP routes through this.
    //
    // Hina.Core.PatchClient owns its own HttpClient because that's an external API
    // surface; if we ever break it, route it through here too.
    public static class SharedHttp
    {
        private static readonly Lazy<HttpClient> _instance = new Lazy<HttpClient>(BuildClient);

        public static HttpClient Instance => _instance.Value;

        private static HttpClient BuildClient()
        {
            HttpClient client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("Hina", Install.HinaVersion.Current));
            return client;
        }
    }
}
