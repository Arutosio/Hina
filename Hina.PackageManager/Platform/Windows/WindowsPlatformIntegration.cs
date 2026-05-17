using System;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Paths;

namespace Hina.PackageManager.Platform.Windows
{
    // Stub. Phase 5 will implement (COM IShellLink, HKCU registry, etc.).
    public sealed class WindowsPlatformIntegration : IPlatformIntegration
    {
        private readonly InstallPaths _paths;
        public WindowsPlatformIntegration(InstallPaths paths) => _paths = paths;

        public string OsId => "windows";
        public string UserBinDir => _paths.UserBinDir;
        public string UserAppsDir => throw new PlatformNotSupportedException("Windows shell integration arrives in Phase 5.");

        public Task<string> CreateMenuShortcut(ShellEntry entry, string appDir, CancellationToken ct) => throw NotYet();
        public Task RemoveMenuShortcut(string evidencePath, CancellationToken ct) => Task.CompletedTask;

        public Task<string> AddToPath(string name, string targetExec, CancellationToken ct) => throw NotYet();
        public Task RemoveFromPath(string evidencePath, CancellationToken ct) => Task.CompletedTask;

        public Task<string> RegisterMimeType(MimeTypeHook hook, string appDir, CancellationToken ct) => throw NotYet();
        public Task UnregisterMimeType(string evidencePath, CancellationToken ct) => Task.CompletedTask;
        public Task<string> RegisterUrlScheme(UrlSchemeHook hook, string appDir, CancellationToken ct) => throw NotYet();
        public Task UnregisterUrlScheme(string evidencePath, CancellationToken ct) => Task.CompletedTask;
        public Task<string> InstallFont(string fontFile, CancellationToken ct) => throw NotYet();
        public Task UninstallFont(string evidencePath, CancellationToken ct) => Task.CompletedTask;
        public Task<string> RegisterAutostart(AutostartHook hook, string appDir, CancellationToken ct) => throw NotYet();
        public Task UnregisterAutostart(string evidencePath, CancellationToken ct) => Task.CompletedTask;

        private static PlatformNotSupportedException NotYet() =>
            new PlatformNotSupportedException("Windows platform integration arrives in Phase 5.");
    }
}
