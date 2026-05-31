using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Descriptor;
using Hina.PackageManager.Hooks;
using Hina.PackageManager.Registry;

namespace Hina.PackageManager.Tests
{
    public class HookExecutorTests
    {
        [Fact]
        public async Task Apply_AddToPath_RecordsBinPathEvidence()
        {
            FakePlatformIntegration p = new();
            HookExecutor exec = new(p);

            HookEvidence ev = await exec.ApplyAsync(
                new AddToPathHook { Name = "demo", Target = "bin/demo" },
                "/apps/demo",
                "demo",
                CancellationToken.None);

            Assert.Equal("addToPath", ev.Action);
            Assert.Equal("/fake/bin/demo", ev.Evidence);
            Assert.Single(p.AddedToPath);
        }

        [Fact]
        public async Task Apply_InstallFont_RecordsAllInstalledPaths()
        {
            FakePlatformIntegration p = new();
            HookExecutor exec = new(p);

            HookEvidence ev = await exec.ApplyAsync(
                new InstallFontHook { Files = { "fonts/A.ttf", "fonts/B.ttf" } },
                "/apps/demo",
                "demo",
                CancellationToken.None);

            Assert.Equal("installFont", ev.Action);
            Assert.Equal("/fake/fonts/A.ttf|/fake/fonts/B.ttf", ev.Evidence);
        }

        [Fact]
        public async Task Apply_Mime_Url_Autostart_DispatchToPlatform()
        {
            FakePlatformIntegration p = new();
            HookExecutor exec = new(p);

            await exec.ApplyAsync(new MimeTypeHook { MimeType = "application/x-foo", Extensions = { ".foo" }, EntryId = "main" }, "/apps/x", "x", CancellationToken.None);
            await exec.ApplyAsync(new UrlSchemeHook { Scheme = "foo", EntryId = "main" }, "/apps/x", "x", CancellationToken.None);
            await exec.ApplyAsync(new AutostartHook { EntryId = "main" }, "/apps/x", "x", CancellationToken.None);

            Assert.Single(p.MimeTypesRegistered);
            Assert.Single(p.UrlSchemesRegistered);
            Assert.Single(p.AutostartRegistered);
        }

        [Fact]
        public async Task Apply_ResolvesEntryExecForMimeUrlAutostart()
        {
            FakePlatformIntegration p = new();
            HookExecutor exec = new(p);

            var entries = new[] { new ShellEntry { Id = "main", Name = "Main", Exec = "bin/app" } };
            string appDir = "/apps/x";
            string expectedExec = System.IO.Path.Combine(appDir, "bin/app");

            await exec.ApplyAsync(new MimeTypeHook { MimeType = "application/x-foo", Extensions = { ".foo" }, EntryId = "main" }, appDir, "x", entries, CancellationToken.None);
            await exec.ApplyAsync(new UrlSchemeHook { Scheme = "foo", EntryId = "main" }, appDir, "x", entries, CancellationToken.None);
            await exec.ApplyAsync(new AutostartHook { EntryId = "main" }, appDir, "x", entries, CancellationToken.None);

            Assert.Equal(expectedExec, p.LastMimeExecAbs);
            Assert.Equal(expectedExec, p.LastUrlExecAbs);
            Assert.Equal(expectedExec, p.LastAutostartExecAbs);
        }

        [Fact]
        public async Task Undo_InstallFont_UninstallsEveryFontInEvidence()
        {
            FakePlatformIntegration p = new();
            HookExecutor exec = new(p);

            HookEvidence ev = new HookEvidence
            {
                Action = "installFont",
                Evidence = "/fake/fonts/A.ttf|/fake/fonts/B.ttf"
            };
            await exec.UndoAsync(ev, CancellationToken.None);

            Assert.Equal(new[] { "/fake/fonts/A.ttf", "/fake/fonts/B.ttf" }, p.UninstalledFonts);
        }

        [Fact]
        public async Task Undo_UnknownAction_IsNoOp()
        {
            FakePlatformIntegration p = new();
            HookExecutor exec = new(p);

            // No exception — unknown actions are tolerated for forward compatibility.
            await exec.UndoAsync(new HookEvidence { Action = "futureAction", Evidence = "x" }, CancellationToken.None);
        }
    }
}
