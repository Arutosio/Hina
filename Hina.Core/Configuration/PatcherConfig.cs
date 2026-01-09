using System;

namespace Hina.Core.Configuration
{
    // Central runtime config for the patcher.
    public sealed class PatcherConfig
    {
        public Uri BaseUrl { get; init; } = new Uri("http://localhost/");
        public string Channel { get; init; } = "stable";
        public int Concurrency { get; init; } = 4;
        public int ChunkSize { get; init; } = 64 * 1024;
        public bool Verify { get; init; } = true;
        public bool Backup { get; init; } = true;
        public string? TrustedPublicKey { get; init; }
    }
}
