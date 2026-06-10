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

        // Test seam: when set, CreateMenuShortcut throws for this entry id (to exercise the
        // update add-phase failure / rollback path). Other ids still succeed.
        public string? ThrowOnCreateShortcutId { get; set; }

        // Test seam: when set, RemoveMenuShortcut cancels this source and throws — simulates
        // the user pressing Ctrl+C while the platform integration step is in flight.
        public CancellationTokenSource? CancelOnRemoveShortcut { get; set; }

        // Same seam for CreateMenuShortcut (update add-phase cancellation).
        public CancellationTokenSource? CancelOnCreateShortcut { get; set; }

        // Cancels the source but completes normally — simulates Ctrl+C arriving in the
        // window between the last add-phase step and the final registry write.
        public CancellationTokenSource? CancelAfterCreateShortcut { get; set; }

        public Task<string> CreateMenuShortcut(ShellEntry entry, string appDir, CancellationToken ct)
        {
            if (CancelOnCreateShortcut != null)
            {
                CancelOnCreateShortcut.Cancel();
                ct.ThrowIfCancellationRequested();
            }
            CancelAfterCreateShortcut?.Cancel();
            if (ThrowOnCreateShortcutId != null && entry.Id == ThrowOnCreateShortcutId)
            {
                throw new System.InvalidOperationException($"Simulated failure creating shortcut '{entry.Id}'.");
            }
            string evidence = $"/fake/apps/{entry.Id}.desktop";
            CreatedShortcuts.Add(evidence);
            return Task.FromResult(evidence);
        }

        public Task RemoveMenuShortcut(string evidencePath, CancellationToken ct)
        {
            if (CancelOnRemoveShortcut != null)
            {
                CancelOnRemoveShortcut.Cancel();
                ct.ThrowIfCancellationRequested();
            }
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

        public Task<string> RegisterMimeType(MimeTypeHook hook, string appDir, string? entryExecAbs, CancellationToken ct)
        {
            string evidence = $"/fake/mime/{hook.MimeType}";
            MimeTypesRegistered.Add(evidence);
            LastMimeExecAbs = entryExecAbs;
            return Task.FromResult(evidence);
        }

        public Task UnregisterMimeType(string evidencePath, CancellationToken ct) => Task.CompletedTask;

        public Task<string> RegisterUrlScheme(UrlSchemeHook hook, string appDir, string? entryExecAbs, CancellationToken ct)
        {
            string evidence = $"/fake/url/{hook.Scheme}";
            UrlSchemesRegistered.Add(evidence);
            LastUrlExecAbs = entryExecAbs;
            return Task.FromResult(evidence);
        }

        // Captured resolved exec paths so tests can assert HookExecutor passed the right one.
        public string? LastMimeExecAbs { get; private set; }
        public string? LastUrlExecAbs { get; private set; }
        public string? LastAutostartExecAbs { get; private set; }

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

        public Task<string> RegisterAutostart(AutostartHook hook, string appDir, string? entryExecAbs, CancellationToken ct)
        {
            string evidence = $"/fake/autostart/{hook.EntryId}";
            AutostartRegistered.Add(evidence);
            LastAutostartExecAbs = entryExecAbs;
            return Task.FromResult(evidence);
        }

        public Task UnregisterAutostart(string evidencePath, CancellationToken ct) => Task.CompletedTask;
    }
}
