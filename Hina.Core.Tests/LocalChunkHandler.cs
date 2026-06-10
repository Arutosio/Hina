using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Hina.Core.Tests
{
    // HttpMessageHandler that serves manifest.json and chunk files from a local
    // build output directory, simulating the remote HTTP server.
    internal sealed class LocalChunkHandler : HttpMessageHandler
    {
        private readonly string _buildOutputDir;

        public LocalChunkHandler(string buildOutputDir)
        {
            _buildOutputDir = buildOutputDir;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath.TrimStart('/');

            // Manifest requests.
            if (path.StartsWith("manifest", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                string manifestPath = Path.Combine(_buildOutputDir, "manifest.json");
                if (File.Exists(manifestPath))
                {
                    byte[] data = File.ReadAllBytes(manifestPath);
                    var response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(data)
                    };
                    response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                    return Task.FromResult(response);
                }
            }

            // Chunk requests: chunks/{bucket}/{hash}.chunk.br
            if (path.StartsWith("chunks/", StringComparison.OrdinalIgnoreCase))
            {
                string chunkPath = Path.Combine(_buildOutputDir, path.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(chunkPath))
                {
                    byte[] data = File.ReadAllBytes(chunkPath);
                    var response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(data)
                    };
                    return Task.FromResult(response);
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
