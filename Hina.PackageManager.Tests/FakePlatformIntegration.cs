using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Platform;

namespace Hina.PackageManager.Tests
{
    // In-memory IPlatformIntegration that records calls and emits synthetic evidence strings.
    // Used by HookExecutorTests, InstallTransactionTests, InstallServiceTests.
    public sealed class FakePlatformIntegration : IPlatformIntegration
    {
        public List<string> CreatedShortcuts { get; } = new();
        public List<string> RemovedShortcuts { get; } = new();
        public List<string> AddedToPath { get; } = new();
        public List<string> RemovedFromPath { get; } = new();
        public List<string> InstalledFonts { get; } = new();
        public List<string> UninstalledFonts { get; } = new();
        public List<string> MimeTypesRegistered { get; } = new();
        public List<string> UrlSchemesRegistered { get; } = new();
        public List<string> AutostartRegistered { get; } = new();

        public string OsId => "fake";
        public string UserBinDir => "/fake/bin";
        public string UserAppsDir => "/fake/apps";

        public Task<string> CreateMenuShortcut(ShellEntry entry, string appDir, CancellationToken ct)
        {
            string evidence = $"/fake/apps/{entry.Id}.desktop";
            CreatedShortcuts.Add(evidence);
            return Task.FromResult(evidence);
        }

        public Task RemoveMenuShortcut(string evidencePath, CancellationToken ct)
        {
            RemovedShortcuts.Add(evidencePath);
            return Task.CompletedTask;
        }

        public Task<string> AddToPath(string name, string targetExec, CancellationToken ct)
        {
            string evidence = $"/fake/bin/{name}";
            AddedToPath.Add(evidence);
            return Task.FromResult(evidence);
        }

        public Task RemoveFromPath(string evidencePath, CancellationToken ct)
        {
            RemovedFromPath.Add(evidencePath);
            return Task.CompletedTask;
        }

        public Task<string> RegisterMimeType(MimeTypeHook hook, string appDir, CancellationToken ct)
        {
            string evidence = $"/fake/mime/{hook.MimeType}";
            MimeTypesRegistered.Add(evidence);
            return Task.FromResult(evidence);
        }

        public Task UnregisterMimeType(string evidencePath, CancellationToken ct) => Task.CompletedTask;

        public Task<string> RegisterUrlScheme(UrlSchemeHook hook, string appDir, CancellationToken ct)
        {
            string evidence = $"/fake/url/{hook.Scheme}";
            UrlSchemesRegistered.Add(evidence);
            return Task.FromResult(evidence);
        }

        public Task UnregisterUrlScheme(string evidencePath, CancellationToken ct) => Task.CompletedTask;

        public Task<string> InstallFont(string fontFile, CancellationToken ct)
        {
            string evidence = $"/fake/fonts/{System.IO.Path.GetFileName(fontFile)}";
            InstalledFonts.Add(evidence);
            return Task.FromResult(evidence);
        }

        public Task UninstallFont(string evidencePath, CancellationToken ct)
        {
            UninstalledFonts.Add(evidencePath);
            return Task.CompletedTask;
        }

        public Task<string> RegisterAutostart(AutostartHook hook, string appDir, CancellationToken ct)
        {
            string evidence = $"/fake/autostart/{hook.EntryId}";
            AutostartRegistered.Add(evidence);
            return Task.FromResult(evidence);
        }

        public Task UnregisterAutostart(string evidencePath, CancellationToken ct) => Task.CompletedTask;
    }
}
