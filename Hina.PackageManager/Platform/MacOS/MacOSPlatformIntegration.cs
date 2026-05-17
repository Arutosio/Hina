using System;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Paths;

namespace Hina.PackageManager.Platform.MacOS
{
    // Stub. Phase 6 will implement (LaunchAgents .plist, lsregister, ~/Applications).
    public sealed class MacOSPlatformIntegration : IPlatformIntegration
    {
        private readonly InstallPaths _paths;
        public MacOSPlatformIntegration(InstallPaths paths) => _paths = paths;

        public string OsId => "macos";
        public string UserBinDir => _paths.UserBinDir;
        public string UserAppsDir => throw new PlatformNotSupportedException("macOS shell integration arrives in Phase 6.");

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
            new PlatformNotSupportedException("macOS platform integration arrives in Phase 6.");
    }
}
