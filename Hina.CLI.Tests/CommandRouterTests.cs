using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hina.CLI;
using Hina.PackageManager.Paths;
using Hina.PackageManager.Registry;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hina.CLI.Tests
{
    // Smoke tests for verb routing + argument parsing + the read-only registry-lock path.
    // These drive CommandRouter against a CommandContext rooted in a temp dir, so no process
    // is spawned and no network/real install state is touched. They exercise the routing and
    // usage/exit-code contract that previously had zero coverage (#12).
    public sealed class CommandRouterTests : IDisposable
    {
        private readonly string _root;

        public CommandRouterTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "hina-cli-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }

        private CommandContext Ctx() => new CommandContext(
            InstallPaths.ForRoot(_root),
            NullLogger.Instance,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        private Task<int> Dispatch(params string[] args) => CommandRouter.DispatchAsync(Ctx(), args);

        [Fact]
        public async Task UnknownCommand_ReturnsUsageError()
        {
            Assert.Equal(2, await Dispatch("frobnicate"));
        }

        [Fact]
        public async Task List_EmptyRegistry_ReturnsZero()
        {
            Assert.Equal(0, await Dispatch("list"));
        }

        [Fact]
        public async Task ListAlias_Ls_RoutesToList()
        {
            Assert.Equal(0, await Dispatch("ls"));
        }

        [Fact]
        public async Task Info_NoName_ReturnsUsageError()
        {
            Assert.Equal(2, await Dispatch("info"));
        }

        [Fact]
        public async Task Info_UnknownApp_ReturnsNotFound()
        {
            Assert.Equal(1, await Dispatch("info", "nope"));
        }

        [Fact]
        public async Task Which_NoName_ReturnsUsageError()
        {
            Assert.Equal(2, await Dispatch("which"));
        }

        [Fact]
        public async Task Install_NoUrl_ReturnsUsageError()
        {
            Assert.Equal(2, await Dispatch("install"));
        }

        [Fact]
        public async Task Install_InvalidUrl_ReturnsUsageError()
        {
            Assert.Equal(2, await Dispatch("install", "not a url"));
        }

        [Fact]
        public async Task Uninstall_NoName_ReturnsUsageError()
        {
            Assert.Equal(2, await Dispatch("uninstall"));
        }

        [Fact]
        public async Task Reinstall_NoName_ReturnsUsageError()
        {
            Assert.Equal(2, await Dispatch("reinstall"));
        }

        [Fact]
        public async Task Verify_EmptyRegistry_ReturnsZero()
        {
            Assert.Equal(0, await Dispatch("verify"));
        }

        [Fact]
        public async Task Dev_NoSubcommand_ReturnsUsageError()
        {
            Assert.Equal(2, await Dispatch("dev"));
        }

        [Fact]
        public async Task Version_ReturnsZero()
        {
            Assert.Equal(0, await Dispatch("version"));
        }

        // ---- sandbox-era verbs (run / perms / repair / dev sandbox-run) ----

        [Fact]
        public async Task Run_NoApp_ReturnsUsageError()
        {
            Assert.Equal(2, await Dispatch("run"));
        }

        [Fact]
        public async Task Run_UnknownApp_ReturnsNotFound()
        {
            Assert.Equal(1, await Dispatch("run", "ghost"));
        }

        [Fact]
        public async Task Perms_EmptyRegistry_PrintsTableReturnsZero()
        {
            Assert.Equal(0, await Dispatch("perms"));
        }

        [Fact]
        public async Task Perms_ListKeyword_ReturnsZero()
        {
            Assert.Equal(0, await Dispatch("perms", "list"));
        }

        [Theory]
        [InlineData("permissions")]
        [InlineData("permessi")]
        public async Task Perms_Aliases_RouteToPerms(string alias)
        {
            Assert.Equal(0, await Dispatch(alias));
        }

        [Fact]
        public async Task Perms_UnknownApp_ReturnsNotFound()
        {
            Assert.Equal(1, await Dispatch("perms", "ghost"));
        }

        [Fact]
        public async Task Repair_Alias_RoutesToVerify_EmptyRegistryReturnsZero()
        {
            Assert.Equal(0, await Dispatch("repair"));
        }

        [Fact]
        public async Task Check_UnknownSubword_ReturnsUsageError()
        {
            // `hina check <not-update>` is the two-word router branch's failure path —
            // deterministic and offline (the `check update` form would hit the network).
            Assert.Equal(2, await Dispatch("check", "frobnicate"));
        }

        [Fact]
        public async Task DevSandboxRun_MissingAppDir_ReturnsUsageError()
        {
            Assert.Equal(2, await Dispatch("dev", "sandbox-run", "--", "/bin/true"));
        }

        [Fact]
        public async Task DevSandboxRun_MissingCommandSeparator_ReturnsUsageError()
        {
            Assert.Equal(2, await Dispatch("dev", "sandbox-run", "--app-dir", _root));
        }

        [Fact]
        public async Task ReadOnly_FutureSchemaRegistry_ReturnsErrorCodeNotCrash()
        {
            // A registry written by a newer Hina makes RegistryStore.Load throw
            // RegistrySchemaException. The router's top-level catch must turn that into a
            // clean exit code, not an unhandled stack trace.
            await File.WriteAllTextAsync(
                InstallPaths.ForRoot(_root).RegistryFile,
                "{\"schemaVersion\":999,\"apps\":{}}");

            Assert.Equal(2, await Dispatch("list"));
        }

        // #7: a read-only command reads the registry under the shared lock. After writing a
        // registry row, `info <name>` and `which <name>` must observe it (exit 0).
        [Fact]
        public async Task ReadOnly_ReadsRegistryWrittenUnderLock()
        {
            InstallPaths paths = InstallPaths.ForRoot(_root);
            Registry registry = new Registry();
            registry.Apps["demo"] = new InstalledApp
            {
                Name = "demo",
                InstalledVersion = "1.0.0",
                InstallPath = Path.Combine(paths.AppsRoot, "demo"),
                DescriptorUrl = "https://example.com/hina.app.json",
                BaseUrl = "https://example.com",
                Channel = "stable",
                PublicKey = "AAAA",
                InstalledAt = DateTimeOffset.UnixEpoch,
                LastUpdatedAt = DateTimeOffset.UnixEpoch
            };
            await new RegistryStore(paths.RegistryFile).SaveAsync(registry);

            Assert.Equal(0, await Dispatch("info", "demo"));
            Assert.Equal(0, await Dispatch("which", "demo"));
            Assert.Equal(0, await Dispatch("list"));
        }
    }
}
