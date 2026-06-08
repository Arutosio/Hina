using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Descriptor;

namespace Hina.PackageManager.Platform
{
    // Per-OS shell + filesystem operations. Every Create/Register returns an "evidence" string —
    // the actual path/key/file written — which the registry stores and replays at uninstall time.
    // Every Remove/Unregister is fail-soft.
    public interface IPlatformIntegration
    {
        string OsId { get; }
        string UserBinDir { get; }
        string UserAppsDir { get; }

        Task<string> CreateMenuShortcut(ShellEntry entry, string appDir, CancellationToken ct);
        // Sandbox-aware overload: when launchOverride is non-null the shortcut's launch
        // command is set to it verbatim (e.g. "hina run app entry") instead of pointing
        // directly at the app binary, so launches route through Hina's sandbox. Default
        // impl ignores the override so platforms without a sandbox backend are unaffected.
        Task<string> CreateMenuShortcut(ShellEntry entry, string appDir, string? launchOverride, CancellationToken ct)
            => CreateMenuShortcut(entry, appDir, ct);
        Task RemoveMenuShortcut(string evidencePath, CancellationToken ct);

        Task<string> AddToPath(string name, string targetExec, CancellationToken ct);
        Task RemoveFromPath(string evidencePath, CancellationToken ct);

        // Phase 4 hooks. `entryExecAbs` is the absolute path to the executable of the entry the
        // hook references (resolved by HookExecutor from the descriptor's entries[]); the OS needs
        // it to write a working launch command. Null only if the entry can't be resolved.
        Task<string> RegisterMimeType(MimeTypeHook hook, string appDir, string? entryExecAbs, CancellationToken ct);
        Task UnregisterMimeType(string evidencePath, CancellationToken ct);

        Task<string> RegisterUrlScheme(UrlSchemeHook hook, string appDir, string? entryExecAbs, CancellationToken ct);
        Task UnregisterUrlScheme(string evidencePath, CancellationToken ct);

        Task<string> InstallFont(string fontFile, CancellationToken ct);
        // Per-app font install: the destination filename is prefixed with the app
        // name so two apps shipping `Foo.ttf` no longer collide and uninstall of
        // one cannot remove the other's font. Default impl forwards to the legacy
        // overload so existing platforms don't break — each impl overrides.
        Task<string> InstallFont(string fontFile, string appName, CancellationToken ct) => InstallFont(fontFile, ct);
        Task UninstallFont(string evidencePath, CancellationToken ct);

        Task<string> RegisterAutostart(AutostartHook hook, string appDir, string? entryExecAbs, CancellationToken ct);
        Task UnregisterAutostart(string evidencePath, CancellationToken ct);

        // Used by `hina verify`. Returns true if the recorded evidence no longer
        // describes a live side-effect (file missing, symlink target gone, registry
        // key absent). action is the HookEvidence.Action discriminator or one of
        // the shellEntry / shellEntryLink synthetic actions documented in
        // RegistryVerifier.
        bool IsEvidenceDangling(string action, string evidence) => false;
    }
}
