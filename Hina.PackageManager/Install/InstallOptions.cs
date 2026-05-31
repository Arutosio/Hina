using System;

namespace Hina.PackageManager.Install
{
    public sealed class InstallOptions
    {
        // When true, accept HTTP descriptor URLs (default: HTTPS only).
        public bool AllowInsecure { get; init; }

        // Called on first-time install for TOFU acceptance of the publisher's key.
        // The CLI always sets this to an interactive prompt.
        public Func<TrustPrompt, bool>? OnFirstTimeTrust { get; init; }

        // Fail-closed default: with no OnFirstTimeTrust callback, a first-time install is
        // refused rather than auto-trusting the publisher's key. Set true to auto-accept on
        // first use (the old permissive behaviour) when running unattended without a prompt.
        public bool AssumeTrustOnFirstUse { get; init; }

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
