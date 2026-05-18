namespace Hina.PackageManager.Install
{
    public sealed class UpdateOptions
    {
        // Permit the descriptor's declared publicKey to differ from the locally-pinned key.
        // Without this, a key change at update time is rejected (potential key-rotation attack).
        public bool AllowRotateKey { get; init; }

        // Force PatchClient to run even when the descriptor version hasn't changed (useful
        // when you suspect the local install is corrupted).
        public bool Force { get; init; }

        // How many apps UpdateAllAsync updates concurrently. Each per-app update is
        // still serial within itself; this just lets the registry-of-N walk overlap
        // network and disk I/O across apps. Default 4 mirrors PatchClient.Concurrency.
        public int MaxParallelism { get; init; } = 4;

        // Tuning for fetch + chunk-download resilience on flaky / mobile networks.
        public NetworkOptions Network { get; init; } = new NetworkOptions();
    }

    public sealed class UpdateResult
    {
        public string Name { get; init; } = string.Empty;
        public string FromVersion { get; init; } = string.Empty;
        public string ToVersion { get; init; } = string.Empty;
        public UpdateStatus Status { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    public enum UpdateStatus
    {
        Updated,
        AlreadyUpToDate,
        Failed
    }
}
