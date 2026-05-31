using System;
using Hina.Core.Configuration;

namespace Hina.PackageManager.Install
{
    // Network-tuning knobs shared by InstallOptions and UpdateOptions. Defaults match
    // PatcherConfig defaults; CLI flags `--retries / --connect-timeout / --request-timeout`
    // surface them to the user. Useful on flaky / mobile / IP-changing connections
    // where the engine's defaults aren't aggressive enough.
    public sealed class NetworkOptions
    {
        public int MaxRetries { get; init; } = 8;
        public int RetryBaseDelayMs { get; init; } = 1000;
        public int MaxRetryDelayMs { get; init; } = 30_000;
        public int ConnectTimeoutMs { get; init; } = 10_000;
        public int RequestTimeoutMs { get; init; } = 60_000;
        // Socket recycle interval — the knob that lets a long session survive IP changes /
        // mobile hand-offs. Was previously frozen at the PatcherConfig default because the
        // install/update mapping never propagated it.
        public int PooledConnectionLifetimeMs { get; init; } = 60_000;

        // Single source of truth for descriptor + network → PatcherConfig. InstallService and
        // UpdateService share this so the mapping (and every field in it) can't drift; the only
        // per-call difference is whether backups are kept.
        public PatcherConfig ToPatchConfig(string baseUrl, string channel, string trustedPublicKey, bool backup)
        {
            return new PatcherConfig
            {
                BaseUrl = new Uri(baseUrl),
                Channel = channel,
                TrustedPublicKey = trustedPublicKey,
                Verify = true,
                Backup = backup,
                MaxRetries = MaxRetries,
                RetryBaseDelayMs = RetryBaseDelayMs,
                MaxRetryDelayMs = MaxRetryDelayMs,
                ConnectTimeoutMs = ConnectTimeoutMs,
                RequestTimeoutMs = RequestTimeoutMs,
                PooledConnectionLifetimeMs = PooledConnectionLifetimeMs
            };
        }
    }
}
