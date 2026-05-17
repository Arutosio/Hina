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
        Task RemoveMenuShortcut(string evidencePath, CancellationToken ct);

        Task<string> AddToPath(string name, string targetExec, CancellationToken ct);
        Task RemoveFromPath(string evidencePath, CancellationToken ct);

        // Phase 4 hooks. Stubs throw PlatformNotSupportedException until implemented.
        Task<string> RegisterMimeType(MimeTypeHook hook, string appDir, CancellationToken ct);
        Task UnregisterMimeType(string evidencePath, CancellationToken ct);

        Task<string> RegisterUrlScheme(UrlSchemeHook hook, string appDir, CancellationToken ct);
        Task UnregisterUrlScheme(string evidencePath, CancellationToken ct);

        Task<string> InstallFont(string fontFile, CancellationToken ct);
        Task UninstallFont(string evidencePath, CancellationToken ct);

        Task<string> RegisterAutostart(AutostartHook hook, string appDir, CancellationToken ct);
        Task UnregisterAutostart(string evidencePath, CancellationToken ct);
    }
}
