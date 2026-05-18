using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Hooks;
using Hina.PackageManager.Install;
using Hina.PackageManager.Registry;

namespace Hina.PackageManager.Tests
{
    public class InstallTransactionTests : IDisposable
    {
        private readonly string _tempDir;

        public InstallTransactionTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "hina-tx-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task Rollback_UnwindsInReverseOrder()
        {
            FakePlatformIntegration platform = new();
            HookExecutor hooks = new(platform);
            InstallTransaction tx = new(platform, hooks);

            string appDir = Path.Combine(_tempDir, "demo");
            Directory.CreateDirectory(appDir);

            tx.RecordAppDirCreated(appDir);
            tx.RecordShellEntry("/fake/apps/main.desktop");
            tx.RecordHook(new HookEvidence { Action = "addToPath", Evidence = "/fake/bin/demo" });

            await tx.RollbackAsync(CancellationToken.None);

            // Hook undone, then shortcut removed, then app dir deleted.
            Assert.Equal(new[] { "/fake/bin/demo" }, platform.RemovedFromPath);
            Assert.Equal(new[] { "/fake/apps/main.desktop" }, platform.RemovedShortcuts);
            Assert.False(Directory.Exists(appDir));
        }

        [Fact]
        public async Task Rollback_DoesNotTouchStepsNeverRecorded()
        {
            FakePlatformIntegration platform = new();
            HookExecutor hooks = new(platform);
            InstallTransaction tx = new(platform, hooks);

            string appDir = Path.Combine(_tempDir, "partial");
            Directory.CreateDirectory(appDir);

            tx.RecordAppDirCreated(appDir);
            // Intentionally no shell-entry or hook recorded — the transaction must not
            // try to undo work that never happened.

            await tx.RollbackAsync(CancellationToken.None);

            Assert.Empty(platform.RemovedShortcuts);
            Assert.Empty(platform.RemovedFromPath);
            Assert.False(Directory.Exists(appDir));
        }

        [Fact]
        public async Task Rollback_FailSoft_ContinuesPastDeletedAppDir()
        {
            FakePlatformIntegration platform = new();
            HookExecutor hooks = new(platform);
            InstallTransaction tx = new(platform, hooks);

            string ghostAppDir = Path.Combine(_tempDir, "ghost");
            // App dir never actually created — recording is fine, rollback must not throw.
            tx.RecordAppDirCreated(ghostAppDir);
            tx.RecordShellEntry("/fake/apps/x.desktop");

            await tx.RollbackAsync(CancellationToken.None);

            Assert.Single(platform.RemovedShortcuts);
        }
    }
}
