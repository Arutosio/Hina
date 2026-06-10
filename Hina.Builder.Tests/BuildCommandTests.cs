using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hina.Builder;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hina.Builder.Tests
{
    public sealed class BuildCommandTests : IDisposable
    {
        private readonly string _base;
        private readonly string _input;
        private readonly string _out;

        public BuildCommandTests()
        {
            _base = Path.Combine(Path.GetTempPath(), "hina-build-" + Guid.NewGuid().ToString("N"));
            _input = Path.Combine(_base, "in");
            _out = Path.Combine(_base, "out");
            Directory.CreateDirectory(_input);
        }

        public void Dispose()
        {
            try { Directory.Delete(_base, recursive: true); } catch { /* best-effort */ }
        }

        private BuildOptions Opts(string? platform) => new BuildOptions
        {
            Input = _input,
            Output = _out,
            BaseUrl = "https://patch.example.com/",
            Version = "1.0.0",
            ChunkingMode = "cdc",
            Platform = platform
        };

        // A typo'd chunk parameter ('--chunk 0', min > max…) used to escape as an unhandled
        // ArgumentOutOfRangeException with a stack trace — the builder has no top-level
        // catch. It must be a clean usage error (exit 2) instead.
        [Theory]
        [InlineData("fixed", 0, 2048, 65536, 8192)]
        [InlineData("fixed", -5, 2048, 65536, 8192)]
        [InlineData("cdc", 65536, 0, 65536, 8192)]
        [InlineData("cdc", 65536, 2048, 65536, 0)]
        [InlineData("cdc", 65536, 65536, 2048, 8192)]
        public async Task Build_InvalidChunkParams_FailsWithUsageError(string mode, int chunk, int min, int max, int avg)
        {
            File.WriteAllText(Path.Combine(_input, "game"), "payload");
            var opts = new BuildOptions
            {
                Input = _input,
                Output = _out,
                BaseUrl = "https://patch.example.com/",
                Version = "1.0.0",
                ChunkingMode = mode,
                ChunkSize = chunk,
                MinChunk = min,
                MaxChunk = max,
                AvgChunk = avg
            };

            int code = await BuildCommand.RunAsync(opts, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(2, code);
        }

        [Fact]
        public async Task Build_WithPlatform_WritesPlatformManifest()
        {
            File.WriteAllText(Path.Combine(_input, "game"), "linux build payload");

            int code = await BuildCommand.RunAsync(Opts("linux"), NullLogger.Instance, CancellationToken.None);

            Assert.Equal(0, code);
            Assert.True(File.Exists(Path.Combine(_out, "manifest.linux.json")));
            Assert.False(File.Exists(Path.Combine(_out, "manifest.json")));
            Assert.True(Directory.Exists(Path.Combine(_out, "chunks")));
        }

        [Fact]
        public async Task Build_TwoVariants_ShareOneChunkStore()
        {
            // Same content in both variant inputs → the shared chunk store dedupes it.
            File.WriteAllText(Path.Combine(_input, "common.dat"), "shared asset bytes");
            await BuildCommand.RunAsync(Opts("linux"), NullLogger.Instance, CancellationToken.None);

            string input2 = Path.Combine(_base, "in2");
            Directory.CreateDirectory(input2);
            File.WriteAllText(Path.Combine(input2, "common.dat"), "shared asset bytes");
            int code = await BuildCommand.RunAsync(new BuildOptions
            {
                Input = input2,
                Output = _out,
                BaseUrl = "https://patch.example.com/",
                Version = "1.0.0",
                ChunkingMode = "cdc",
                Platform = "windows-x64"
            }, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(0, code);
            Assert.True(File.Exists(Path.Combine(_out, "manifest.linux.json")));
            Assert.True(File.Exists(Path.Combine(_out, "manifest.windows-x64.json")));
            // One shared chunks/ dir under the single output.
            Assert.True(Directory.Exists(Path.Combine(_out, "chunks")));
        }

        [Fact]
        public async Task Build_InvalidPlatformToken_ReturnsUsageError()
        {
            File.WriteAllText(Path.Combine(_input, "game"), "x");
            int code = await BuildCommand.RunAsync(Opts("solaris-sparc"), NullLogger.Instance, CancellationToken.None);
            Assert.Equal(2, code);
        }
    }
}
