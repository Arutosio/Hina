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
        public int MaxRetries { get; init; } = 3;
        public int RetryBaseDelayMs { get; init; } = 1000;

        // Chunking mode: "fixed" (default) or "cdc" (content-defined chunking).
        public string ChunkingMode { get; init; } = "fixed";

        // CDC-specific settings (only used when ChunkingMode is "cdc").
        public int MinChunkSize { get; init; } = 2048;
        public int MaxChunkSize { get; init; } = 64 * 1024;
        public int AvgChunkSize { get; init; } = 8192;
    }
}
