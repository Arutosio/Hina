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

        private async Task SeedApp(string name)
        {
            InstallPaths paths = InstallPaths.ForRoot(_root);
            Registry registry = new Registry();
            registry.Apps[name] = new InstalledApp
            {
                Name = name,
                InstalledVersion = "1.0.0",
                InstallPath = Path.Combine(paths.AppsRoot, name),
                DescriptorUrl = "https://example.com/hina.app.json",
                BaseUrl = "https://example.com",
                Channel = "stable",
                PublicKey = "AAAA",
                InstalledAt = DateTimeOffset.UnixEpoch,
                LastUpdatedAt = DateTimeOffset.UnixEpoch
            };
            await new RegistryStore(paths.RegistryFile).SaveAsync(registry);
        }

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
        public async Task Perms_GrantFlagWithoutValue_ReturnsUsageError()
        {
            // `hina perms <app> --grant` (no path) previously fell through to the read-only
            // detail view and silently dropped the mutation. It must be a usage error.
            await SeedApp("demo");
            Assert.Equal(2, await Dispatch("perms", "demo", "--grant"));
        }

        // BUG-005: `--grant` / `--revoke` without a value swallowed the next flag as a path.
        // e.g. `hina perms demo --grant --revoke /p` handed "--revoke" to ParseGrant as a path
        // instead of rejecting the missing --grant value.
        //
        // Test strategy: the "next flag" used in these tests must itself be a known perms flag
        // (--revoke / --grant) so that FirstUnknownFlag does not short-circuit before the
        // BUG-005 guard runs. An unknown flag like --json would be caught by FirstUnknownFlag
        // first and produce "Unknown flag '--json'", not a --grant/--revoke error.

        [Fact]
        public async Task Perms_GrantFollowedByAnotherFlag_ReturnsUsageError()
        {
            // `hina perms demo --grant --revoke /p` — "--revoke" is a known flag but starts
            // with '-', so it must be rejected as the missing --grant value.
            await SeedApp("demo");
            Assert.Equal(2, await Dispatch("perms", "demo", "--grant", "--revoke", "/tmp/p"));
        }

        [Fact]
        public async Task Perms_RevokeFollowedByAnotherFlag_ReturnsUsageError()
        {
            // `hina perms demo --revoke --grant /p` — "--grant" is a known flag but starts
            // with '-', so it must be rejected as the missing --revoke value.
            await SeedApp("demo");
            Assert.Equal(2, await Dispatch("perms", "demo", "--revoke", "--grant", "/tmp/p"));
        }

        [Fact]
        public async Task Perms_GrantFollowedByFlag_ErrorMessageNamesGrantFlag()
        {
            // The error message must name '--grant' so the user knows which flag is wrong.
            await SeedApp("demo");
            var log = new CapturingLogger();
            var ctx = new CommandContext(InstallPaths.ForRoot(_root), log, NullLoggerFactory.Instance, CancellationToken.None);

            int exit = await CommandRouter.DispatchAsync(ctx, new[] { "perms", "demo", "--grant", "--revoke", "/tmp/p" });

            Assert.Equal(2, exit);
            Assert.Contains(log.Messages, m => m.Contains("--grant"));
        }

        [Fact]
        public async Task Perms_RevokeFollowedByFlag_ErrorMessageNamesRevokeFlag()
        {
            // The error message must name '--revoke'.
            await SeedApp("demo");
            var log = new CapturingLogger();
            var ctx = new CommandContext(InstallPaths.ForRoot(_root), log, NullLoggerFactory.Instance, CancellationToken.None);

            int exit = await CommandRouter.DispatchAsync(ctx, new[] { "perms", "demo", "--revoke", "--grant", "/tmp/p" });

            Assert.Equal(2, exit);
            Assert.Contains(log.Messages, m => m.Contains("--revoke"));
        }

        [Fact]
        public async Task Perms_GrantWithRealPath_DoesNotTreatNextFlagAsPath()
        {
            // Positive case: a real path value is accepted; the flag after it is NOT consumed.
            // `--grant /tmp/x --revoke` still has --revoke with no value → usage error (not a
            // successful grant that consumed "--revoke" as the path).
            await SeedApp("demo");
            Assert.Equal(2, await Dispatch("perms", "demo", "--grant", "/tmp/x", "--revoke"));
        }

        [Fact]
        public async Task Perms_GrantOnlyAccessSuffix_ReturnsUsageErrorNamingTheFlag()
        {
            // `--grant :rw` strips the access suffix down to an empty path; Path.GetFullPath("")
            // then threw ArgumentException and the user saw "The path is empty." with no hint
            // which flag was wrong. It must be a usage error that names --grant.
            await SeedApp("demo");

            var log = new CapturingLogger();
            var ctx = new CommandContext(InstallPaths.ForRoot(_root), log, NullLoggerFactory.Instance, CancellationToken.None);

            int exit = await CommandRouter.DispatchAsync(ctx, new[] { "perms", "demo", "--grant", ":rw" });

            Assert.Equal(2, exit);
            Assert.Contains(log.Messages, m => m.Contains("--grant"));
        }

        private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
        {
            public System.Collections.Generic.List<string> Messages { get; } = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
                TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => Messages.Add(formatter(state, exception));
        }

        [Fact]
        public async Task Dev_InvalidBaseUrl_ReturnsUsageErrorNamingTheFlag()
        {
            // `hina dev check --base "not a url"` reached `new Uri(baseUrl)` and surfaced the
            // raw "Invalid URI: ..." with no hint that --base was the malformed flag.
            var log = new CapturingLogger();
            var ctx = new CommandContext(InstallPaths.ForRoot(_root), log, NullLoggerFactory.Instance, CancellationToken.None);

            int exit = await CommandRouter.DispatchAsync(ctx, new[] { "dev", "check", "--dir", _root, "--base", "not a url" });

            Assert.Equal(2, exit);
            Assert.Contains(log.Messages, m => m.Contains("--base"));
        }

        [Theory]
        [InlineData("update")]
        [InlineData("reinstall")]
        public async Task UpdateAndReinstall_Cancelled_ReturnCancelledExitCode(string verb)
        {
            // Both commands wrapped their work in catch(Exception), converting a Ctrl+C
            // (OperationCanceledException) into "failed" exit 2 — the router's dedicated
            // "Cancelled." path (exit 1) was unreachable. The app must exist so the flow
            // reaches the descriptor fetch, where the cancelled token surfaces as OCE.
            await SeedApp("demo");
            Directory.CreateDirectory(Path.Combine(InstallPaths.ForRoot(_root).AppsRoot, "demo"));
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var ctx = new CommandContext(InstallPaths.ForRoot(_root), NullLogger.Instance, NullLoggerFactory.Instance, cts.Token);

            Assert.Equal(1, await CommandRouter.DispatchAsync(ctx, new[] { verb, "demo" }));
        }

        [Theory]
        [InlineData("perms", "demo", "--grant-all")]
        [InlineData("update", "demo", "--alll")]
        [InlineData("reinstall", "demo", "--rotate-keys")]
        public async Task TypoedFlag_ReturnsUsageErrorNamingTheFlag(params string[] args)
        {
            // install/uninstall already reject unknown flags; update/reinstall/perms silently
            // ignored them — a typo'd --rotate-keys or --allow-downgrade changes behavior
            // with no diagnostic.
            await SeedApp("demo");
            var log = new CapturingLogger();
            var ctx = new CommandContext(InstallPaths.ForRoot(_root), log, NullLoggerFactory.Instance, CancellationToken.None);

            Assert.Equal(2, await CommandRouter.DispatchAsync(ctx, args));
            Assert.Contains(log.Messages, m => m.Contains("Unknown flag"));
        }

        [Fact]
        public async Task Uninstall_UnknownFlag_ReturnsUsageError()
        {
            // `uninstall` takes no flags; a typo must be rejected, not silently ignored
            // (which would otherwise exit 0 as "was not installed").
            Assert.Equal(2, await Dispatch("uninstall", "ghost", "--bogus"));
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
