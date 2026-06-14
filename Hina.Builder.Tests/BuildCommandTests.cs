using System;
using System.IO;
using System.Linq;
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

        [Theory]
        [InlineData(".")]      // --out == --input
        [InlineData("store")]  // --out inside --input
        public async Task Build_OutputInsideInput_FailsWithUsageError(string relOut)
        {
            // InitCommand already refuses an output inside the app folder; build had no such
            // guard. With --out inside --input, the chunk store and manifest land in the input
            // tree, so the NEXT build chunks the previous build's store into the manifest and
            // clients download the whole chunk store as app content.
            await File.WriteAllTextAsync(Path.Combine(_input, "app.bin"), "data");
            string outDir = Path.GetFullPath(Path.Combine(_input, relOut));

            int code = await BuildCommand.RunAsync(new BuildOptions
            {
                Input = _input,
                Output = outDir,
                BaseUrl = "https://patch.example.com/",
                Version = "1.0.0"
            }, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(2, code);
        }

        [Fact]
        public async Task Build_InvalidBaseUrl_FailsWithUsageError()
        {
            // 'not a url' reached new Uri(...) and escaped as a raw UriFormatException.
            await File.WriteAllTextAsync(Path.Combine(_input, "app.bin"), "data");

            int code = await BuildCommand.RunAsync(new BuildOptions
            {
                Input = _input,
                Output = _out,
                BaseUrl = "not a url",
                Version = "1.0.0"
            }, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(2, code);
        }

        [Fact]
        public async Task Build_SignKeyNotBase64_FailsWithUsageError()
        {
            // A wrong file passed as --sign-key (e.g. the .pub or the descriptor itself)
            // escaped as a raw FormatException that never named the file.
            await File.WriteAllTextAsync(Path.Combine(_input, "app.bin"), "data");
            string keyPath = Path.Combine(_base, "not-a-key.txt");
            await File.WriteAllTextAsync(keyPath, "this is not base64!!");
            var log = new CapturingLogger();

            int code = await BuildCommand.RunAsync(new BuildOptions
            {
                Input = _input,
                Output = _out,
                BaseUrl = "https://patch.example.com/",
                SignKeyPath = keyPath,
                Version = "1.0.0"
            }, log, CancellationToken.None);

            Assert.Equal(2, code);
            // BUG-038: the message must point at keygen's real output (<name>.key.b64), not a
            // file name keygen never produces (ed25519.priv.b64).
            Assert.Contains(log.Messages, m => m.Contains(".key.b64"));
            Assert.DoesNotContain(log.Messages, m => m.Contains("ed25519.priv.b64"));
        }

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

        // ---- --common: shared files merged into the variant build ----

        private string MakeCommonDir(params (string RelPath, string Content)[] files)
        {
            string common = Path.Combine(_base, "common");
            Directory.CreateDirectory(common);
            foreach ((string rel, string content) in files)
            {
                string full = Path.Combine(common, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
            }
            return common;
        }

        [Fact]
        public async Task Build_WithCommon_MergesCommonFilesIntoManifest()
        {
            File.WriteAllText(Path.Combine(_input, "game"), "linux binary");
            string common = MakeCommonDir(("data/map0.mul", "map bytes"), ("art.mul", "art bytes"));

            int code = await BuildCommand.RunAsync(new BuildOptions
            {
                Input = _input,
                Output = _out,
                BaseUrl = "https://patch.example.com/",
                Version = "1.0.0",
                ChunkingMode = "cdc",
                Platform = "linux",
                Common = common
            }, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(0, code);
            var manifest = await Hina.Core.Manifest.ManifestSerializer.ReadAsync(
                Path.Combine(_out, "manifest.linux.json"), CancellationToken.None);
            var paths = manifest.Files.Select(f => f.Path).ToList();
            Assert.Contains("game", paths);
            Assert.Contains("data/map0.mul", paths);
            Assert.Contains("art.mul", paths);
            Assert.True(Directory.Exists(Path.Combine(_out, "chunks")));
        }

        [Fact]
        public async Task Build_WithCommon_NoPlatform_WritesMergedManifestJson()
        {
            File.WriteAllText(Path.Combine(_input, "game"), "binary");
            string common = MakeCommonDir(("shared.dat", "shared"));

            int code = await BuildCommand.RunAsync(new BuildOptions
            {
                Input = _input,
                Output = _out,
                BaseUrl = "https://patch.example.com/",
                Version = "1.0.0",
                ChunkingMode = "cdc",
                Common = common
            }, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(0, code);
            var manifest = await Hina.Core.Manifest.ManifestSerializer.ReadAsync(
                Path.Combine(_out, "manifest.json"), CancellationToken.None);
            Assert.Contains(manifest.Files, f => f.Path == "shared.dat");
            Assert.Contains(manifest.Files, f => f.Path == "game");
        }

        [Fact]
        public async Task Build_WithCommon_VariantOverridesCommonFile_VariantWins_AndLogs()
        {
            File.WriteAllText(Path.Combine(_input, "config.ini"), "variant-version-longer");
            string common = MakeCommonDir(("config.ini", "common"));
            var log = new CapturingLogger();

            int code = await BuildCommand.RunAsync(new BuildOptions
            {
                Input = _input,
                Output = _out,
                BaseUrl = "https://patch.example.com/",
                Version = "1.0.0",
                ChunkingMode = "cdc",
                Platform = "linux",
                Common = common
            }, log, CancellationToken.None);

            Assert.Equal(0, code);
            var manifest = await Hina.Core.Manifest.ManifestSerializer.ReadAsync(
                Path.Combine(_out, "manifest.linux.json"), CancellationToken.None);
            var entry = Assert.Single(manifest.Files, f => f.Path == "config.ini");
            Assert.Equal("variant-version-longer".Length, entry.Size);
            Assert.Contains(log.Messages, m => m.Contains("config.ini") && m.Contains("overrides"));
        }

        [Fact]
        public async Task Build_WithCommon_OverriddenCommonChunks_NotWrittenToStore()
        {
            // Learn the overridden common file's chunk names from a throwaway build of the
            // common side alone, then assert none of them landed in the real store.
            File.WriteAllText(Path.Combine(_input, "config.ini"), "variant-version-longer");
            string common = MakeCommonDir(("config.ini", "common-only-content"));

            string refOut = Path.Combine(_base, "ref-out");
            int refCode = await BuildCommand.RunAsync(new BuildOptions
            {
                Input = common,
                Output = refOut,
                BaseUrl = "https://patch.example.com/",
                Version = "1.0.0",
                ChunkingMode = "cdc"
            }, NullLogger.Instance, CancellationToken.None);
            Assert.Equal(0, refCode);
            string[] commonChunks = Directory.GetFiles(Path.Combine(refOut, "chunks"), "*.chunk.br", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .ToArray()!;
            Assert.NotEmpty(commonChunks);

            int code = await BuildCommand.RunAsync(new BuildOptions
            {
                Input = _input,
                Output = _out,
                BaseUrl = "https://patch.example.com/",
                Version = "1.0.0",
                ChunkingMode = "cdc",
                Platform = "linux",
                Common = common
            }, NullLogger.Instance, CancellationToken.None);
            Assert.Equal(0, code);

            string[] storeChunks = Directory.GetFiles(Path.Combine(_out, "chunks"), "*.chunk.br", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .ToArray()!;
            foreach (string chunk in commonChunks)
            {
                Assert.DoesNotContain(chunk, storeChunks);
            }
        }

        [Fact]
        public async Task Build_CommonNotFound_FailsWithUsageError()
        {
            File.WriteAllText(Path.Combine(_input, "game"), "x");
            var log = new CapturingLogger();

            int code = await BuildCommand.RunAsync(new BuildOptions
            {
                Input = _input,
                Output = _out,
                BaseUrl = "https://patch.example.com/",
                Common = Path.Combine(_base, "absent")
            }, log, CancellationToken.None);

            Assert.Equal(2, code);
            Assert.Contains(log.Messages, m => m.Contains("--common"));
        }

        [Theory]
        [InlineData("in/shared")]   // --common inside --input
        [InlineData("in")]          // --common == --input
        public async Task Build_CommonOverlappingInput_FailsWithUsageError(string relCommon)
        {
            File.WriteAllText(Path.Combine(_input, "game"), "x");
            string common = Path.Combine(_base, relCommon.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(common);
            var log = new CapturingLogger();

            int code = await BuildCommand.RunAsync(new BuildOptions
            {
                Input = _input,
                Output = _out,
                BaseUrl = "https://patch.example.com/",
                Common = common
            }, log, CancellationToken.None);

            Assert.Equal(2, code);
            Assert.Contains(log.Messages, m => m.Contains("--common"));
        }

        [Fact]
        public async Task Build_InputInsideCommon_FailsWithUsageError()
        {
            // The whole --input tree would also be enumerated as common content.
            string common = Path.Combine(_base, "tree");
            string input = Path.Combine(common, "variant");
            Directory.CreateDirectory(input);
            File.WriteAllText(Path.Combine(input, "game"), "x");

            int code = await BuildCommand.RunAsync(new BuildOptions
            {
                Input = input,
                Output = _out,
                BaseUrl = "https://patch.example.com/",
                Common = common
            }, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(2, code);
        }

        [Fact]
        public async Task Build_OutInsideCommon_FailsWithUsageError()
        {
            File.WriteAllText(Path.Combine(_input, "game"), "x");
            string common = MakeCommonDir(("shared.dat", "s"));

            int code = await BuildCommand.RunAsync(new BuildOptions
            {
                Input = _input,
                Output = Path.Combine(common, "store"),
                BaseUrl = "https://patch.example.com/",
                Common = common
            }, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(2, code);
        }

        [Fact]
        public async Task Build_CommonInsideOut_FailsWithUsageError()
        {
            File.WriteAllText(Path.Combine(_input, "game"), "x");
            string common = Path.Combine(_out, "common");
            Directory.CreateDirectory(common);

            int code = await BuildCommand.RunAsync(new BuildOptions
            {
                Input = _input,
                Output = _out,
                BaseUrl = "https://patch.example.com/",
                Common = common
            }, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(2, code);
        }

        private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
        {
            public System.Collections.Generic.List<string> Messages { get; } = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
                TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => Messages.Add(formatter(state, exception));
        }
    }
}
