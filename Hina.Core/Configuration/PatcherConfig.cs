using System;

namespace Hina.Core.Configuration
{
    // Central runtime config for the patcher.
    public sealed class PatcherConfig
    {
        public Uri BaseUrl { get; set; } = new Uri("http://localhost/");
        public string Channel { get; set; } = "stable";

        // Variant token (e.g. "macos-arm64") for a multi-platform app, selecting which
        // manifest.<platform>.json to fetch. Null ⇒ legacy single-manifest app.
        public string? Platform { get; set; }
        public int Concurrency { get; set; } = 4;
        public int ChunkSize { get; set; } = 64 * 1024;
        public bool Verify { get; set; } = true;
        public bool Backup { get; set; } = true;
        public string? TrustedPublicKey { get; set; }
        public int MaxRetries { get; set; } = 8;
        public int RetryBaseDelayMs { get; set; } = 1000;
        public int MaxRetryDelayMs { get; set; } = 30_000;

        // Per-TCP-connect timeout. Short so a stalled handshake fails fast and the
        // retry policy kicks in (vs. waiting for the default HttpClient 100s wall).
        public int ConnectTimeoutMs { get; set; } = 10_000;

        // Per-HTTP-request timeout (overall). Bounds how long a single chunk/manifest
        // fetch can hang before being treated as transient and retried.
        public int RequestTimeoutMs { get; set; } = 60_000;

        // Connection pool lifetime. After this, the underlying socket is closed and
        // a fresh DNS lookup + connect runs on the next request. Matters on flaky /
        // mobile / changing-IP links so we don't reuse a dead socket forever.
        public int PooledConnectionLifetimeMs { get; set; } = 60_000;

        // Chunking mode: "fixed" (default) or "cdc" (content-defined chunking).
        public string ChunkingMode { get; set; } = "fixed";

        // CDC-specific settings (only used when ChunkingMode is "cdc").
        public int MinChunkSize { get; set; } = 2048;
        public int MaxChunkSize { get; set; } = 64 * 1024;
        public int AvgChunkSize { get; set; } = 8192;
    }
}
