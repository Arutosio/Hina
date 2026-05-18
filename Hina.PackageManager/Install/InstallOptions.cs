using System;

namespace Hina.PackageManager.Install
{
    public sealed class InstallOptions
    {
        // When true, accept HTTP descriptor URLs (default: HTTPS only).
        public bool AllowInsecure { get; init; }

        // Called on first-time install for TOFU acceptance of the publisher's key.
        // Default impl (null) auto-accepts — CLI overrides with an interactive prompt.
        public Func<TrustPrompt, bool>? OnFirstTimeTrust { get; init; }

        // Tuning for fetch + chunk-download resilience on flaky / mobile networks.
        public NetworkOptions Network { get; init; } = new NetworkOptions();
    }

    public sealed class TrustPrompt
    {
        public string AppName { get; init; } = string.Empty;
        public string Publisher { get; init; } = string.Empty;
        public string DescriptorUrl { get; init; } = string.Empty;
        public string PublicKeyFingerprint { get; init; } = string.Empty;
    }
}
