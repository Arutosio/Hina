using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Configuration;
using Hina.Core.Patching;

namespace Hina.PackageManager.Tests
{
    // Drop-in IPatchClient that "patches" by writing a configured set of files into rootDir.
    // Lets InstallService be tested without spinning up a real HTTP server or builder.
    public sealed class FakePatchClient : IPatchClient
    {
        private readonly Dictionary<string, byte[]> _filesToWrite;

        public FakePatchClient(PatcherConfig config, Dictionary<string, byte[]> filesToWrite)
        {
            Config = config;
            _filesToWrite = filesToWrite;
        }

        public PatcherConfig Config { get; }

        public Task<CheckResult> CheckAsync(string rootDir, CancellationToken ct) =>
            Task.FromResult(new CheckResult { IsUpdateAvailable = true });

        public Task<PatchResult> PatchAsync(string rootDir, CancellationToken ct)
        {
            foreach ((string relative, byte[] bytes) in _filesToWrite)
            {
                string abs = Path.Combine(rootDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
                File.WriteAllBytes(abs, bytes);
            }
            return Task.FromResult(new PatchResult { Success = true });
        }

        public Task<VerifyResult> VerifyAsync(string rootDir, CancellationToken ct) =>
            Task.FromResult(new VerifyResult { Success = true });

        public Task RollbackAsync(string rootDir, CancellationToken ct) => Task.CompletedTask;
    }
}
