using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Net;
using Xunit;

namespace Hina.Core.Tests
{
    // A hostile or corrupt (unsigned) manifest can carry a chunk strong hash shorter than
    // the two characters the bucket path needs. That must surface as a clean manifest
    // error, not an ArgumentOutOfRangeException from Substring.
    public class HttpChunkClientHashTests
    {
        [Theory]
        [InlineData("")]
        [InlineData("a")]
        [InlineData("sha256:")]
        [InlineData("sha256:f")]
        public async Task GetChunkAsync_TruncatedStrongHash_ThrowsInvalidData(string strong)
        {
            var client = new HttpChunkClient(new HttpClient(new NotFoundHandler()));

            await Assert.ThrowsAsync<InvalidDataException>(
                () => client.GetChunkAsync(new Uri("http://test.local/"), strong, CancellationToken.None));
        }

        private sealed class NotFoundHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
