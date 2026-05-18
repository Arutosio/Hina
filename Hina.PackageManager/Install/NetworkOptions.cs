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
    }
}
