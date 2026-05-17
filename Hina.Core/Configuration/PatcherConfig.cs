using System;

namespace Hina.Core.Configuration
{
    // Central runtime config for the patcher.
    public sealed class PatcherConfig
    {
        public Uri BaseUrl { get; set; } = new Uri("http://localhost/");
        public string Channel { get; set; } = "stable";
        public int Concurrency { get; set; } = 4;
        public int ChunkSize { get; set; } = 64 * 1024;
        public bool Verify { get; set; } = true;
        public bool Backup { get; set; } = true;
        public string? TrustedPublicKey { get; set; }
        public int MaxRetries { get; set; } = 3;
        public int RetryBaseDelayMs { get; set; } = 1000;

        // Chunking mode: "fixed" (default) or "cdc" (content-defined chunking).
        public string ChunkingMode { get; set; } = "fixed";

        // CDC-specific settings (only used when ChunkingMode is "cdc").
        public int MinChunkSize { get; set; } = 2048;
        public int MaxChunkSize { get; set; } = 64 * 1024;
        public int AvgChunkSize { get; set; } = 8192;
    }
}
