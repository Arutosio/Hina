using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Hina.PackageManager.Sandbox;
using Xunit;

namespace Hina.PackageManager.Tests
{
    // Unit tests for BUG-018 and BUG-019 network-enforcement logic in
    // LinuxLandlockSandbox. All tests are purely logical — no syscalls, no kernel
    // required, no Linux guard needed — because the tested surface is:
    //   (a) ComputeNetAttr: a static pure method exposed as internal.
    //   (b) Warning disclosure via an injected ILogger captured by FakeLogger.
    //   (c) CanEnforceNetwork: an internal property derived from the ABI field.
    //
    // The one-time static warning guards (_netUnenforceableWarned / _netPartialWarned)
    // are process-wide volatiles.  Because xUnit may run tests in any order and the
    // guards are sticky once set, each test that exercises the warning path constructs
    // a fresh sandbox instance via the internal ctor-with-abi-override helper
    // InternalsForTest, which bypasses ProbeAbi() so tests control the ABI directly.
    public class LinuxLandlockNetworkTests
    {
        // ── ComputeNetAttr table (BUG-018 + BUG-019) ──────────────────────────

        [Fact]
        public void ComputeNetAttr_Abi3_NoRestrict_FsOnly()
        {
            // ABI < 4, restrict=false: only the fs field is relevant.
            var (netHandled, netScoped, attrSize) = LinuxLandlockSandbox.ComputeNetAttr(abi: 3, restrictNetwork: false);

            Assert.Equal(0ul, netHandled);
            Assert.Equal(0ul, netScoped);
            Assert.Equal(sizeof(ulong), attrSize);   // 8 bytes — fs field only
        }

        [Fact]
        public void ComputeNetAttr_Abi3_Restrict_StillFsOnly()
        {
            // BUG-018: even with restrict=true, ABI < 4 cannot enforce network.
            // No net bits must leak into the struct; attrSize must stay at 1 ulong
            // so the kernel does not see unknown fields (EINVAL guard).
            var (netHandled, netScoped, attrSize) = LinuxLandlockSandbox.ComputeNetAttr(abi: 3, restrictNetwork: true);

            Assert.Equal(0ul, netHandled);
            Assert.Equal(0ul, netScoped);
            Assert.Equal(sizeof(ulong), attrSize);   // 8 bytes — fs field only
        }

        [Fact]
        public void ComputeNetAttr_Abi4_Restrict_TcpOnlyNoScoped()
        {
            // ABI 4 (kernel 6.7): TCP bind+connect handled; scoped not yet available.
            var (netHandled, netScoped, attrSize) = LinuxLandlockSandbox.ComputeNetAttr(abi: 4, restrictNetwork: true);

            Assert.NotEqual(0ul, netHandled);   // NET_BIND_TCP | NET_CONNECT_TCP
            Assert.Equal(0ul, netScoped);
            Assert.Equal(2 * sizeof(ulong), attrSize);   // 16 bytes — fs + net
        }

        [Fact]
        public void ComputeNetAttr_Abi4_NoRestrict_FsOnly()
        {
            // restrict=false must produce zero net bits regardless of ABI.
            var (netHandled, netScoped, attrSize) = LinuxLandlockSandbox.ComputeNetAttr(abi: 4, restrictNetwork: false);

            Assert.Equal(0ul, netHandled);
            Assert.Equal(0ul, netScoped);
            Assert.Equal(sizeof(ulong), attrSize);
        }

        [Fact]
        public void ComputeNetAttr_Abi5_Restrict_TcpPlusAbstractUnix()
        {
            // ABI 5 (kernel 6.8): TCP + abstract UNIX socket scoping.
            var (netHandled, netScoped, attrSize) = LinuxLandlockSandbox.ComputeNetAttr(abi: 5, restrictNetwork: true);

            Assert.NotEqual(0ul, netHandled);   // TCP bits
            Assert.NotEqual(0ul, netScoped);    // LANDLOCK_SCOPE_ABSTRACT_UNIX_SOCKET
            Assert.Equal(3 * sizeof(ulong), attrSize);   // 24 bytes — fs + net + scoped
        }

        [Fact]
        public void ComputeNetAttr_Abi6_Restrict_SameSizeAsAbi5()
        {
            // Future ABI > 5: attrSize stays at 3 * ulong until we add new fields.
            var (netHandled, netScoped, attrSize) = LinuxLandlockSandbox.ComputeNetAttr(abi: 6, restrictNetwork: true);

            Assert.NotEqual(0ul, netHandled);
            Assert.NotEqual(0ul, netScoped);
            Assert.Equal(3 * sizeof(ulong), attrSize);
        }

        // ── BUG-018: Warning disclosure when ABI < 4 ──────────────────────────

        [Fact]
        public void Launch_Abi3_RestrictNetwork_EmitsWarning()
        {
            // On ABI < 4 with RestrictNetwork=true the sandbox must emit at least
            // one Warning-level log message that mentions the enforcement gap.
            // We use the internal ctor shim so no real syscall is involved.
            FakeLogger logger = new FakeLogger();
            LinuxLandlockSandbox sandbox = LinuxLandlockSandbox.CreateForTest(abi: 3, logger);

            // The warning is emitted inside Launch (before any syscall path), so we
            // can verify it without actually calling execv.  We invoke
            // EmitNetworkDisclosureWarnings, an internal helper that runs only the
            // disclosure block from Launch, keeping P/Invoke out of unit tests.
            sandbox.EmitNetworkDisclosureWarnings(restrictNetwork: true);

            Assert.Contains(logger.Messages, m =>
                m.Level == LogLevel.Warning &&
                m.Text.Contains("FULL network access"));
        }

        [Fact]
        public void Launch_Abi3_RestrictNetwork_WarningEmittedOnlyOnce()
        {
            // The one-time guard must prevent duplicate warnings across multiple
            // launches — shortcut apps call `hina run` on every icon click.
            FakeLogger logger = new FakeLogger();
            LinuxLandlockSandbox sandbox = LinuxLandlockSandbox.CreateForTest(abi: 3, logger);

            sandbox.EmitNetworkDisclosureWarnings(restrictNetwork: true);
            sandbox.EmitNetworkDisclosureWarnings(restrictNetwork: true);
            sandbox.EmitNetworkDisclosureWarnings(restrictNetwork: true);

            int warnings = logger.Messages.FindAll(m =>
                m.Level == LogLevel.Warning &&
                m.Text.Contains("FULL network access")).Count;

            Assert.Equal(1, warnings);
        }

        [Fact]
        public void Launch_Abi3_NoRestrict_NoWarning()
        {
            // If RestrictNetwork is false, no network warning should fire at all.
            FakeLogger logger = new FakeLogger();
            LinuxLandlockSandbox sandbox = LinuxLandlockSandbox.CreateForTest(abi: 3, logger);

            sandbox.EmitNetworkDisclosureWarnings(restrictNetwork: false);

            Assert.DoesNotContain(logger.Messages, m =>
                m.Text.Contains("network") || m.Text.Contains("Network"));
        }

        // ── BUG-019: Partial-enforcement warning when ABI >= 4 ────────────────

        [Fact]
        public void Launch_Abi4_RestrictNetwork_EmitsPartialWarning()
        {
            // ABI >= 4: network restriction is active but PARTIAL (UDP not blocked).
            // The sandbox must emit a Warning-level message that documents the gap.
            FakeLogger logger = new FakeLogger();
            LinuxLandlockSandbox sandbox = LinuxLandlockSandbox.CreateForTest(abi: 4, logger);

            sandbox.EmitNetworkDisclosureWarnings(restrictNetwork: true);

            Assert.Contains(logger.Messages, m =>
                m.Level == LogLevel.Warning &&
                m.Text.Contains("PARTIAL"));
        }

        [Fact]
        public void Launch_Abi4_RestrictNetwork_PartialWarningEmittedOnlyOnce()
        {
            FakeLogger logger = new FakeLogger();
            LinuxLandlockSandbox sandbox = LinuxLandlockSandbox.CreateForTest(abi: 4, logger);

            sandbox.EmitNetworkDisclosureWarnings(restrictNetwork: true);
            sandbox.EmitNetworkDisclosureWarnings(restrictNetwork: true);

            int warnings = logger.Messages.FindAll(m =>
                m.Level == LogLevel.Warning &&
                m.Text.Contains("PARTIAL")).Count;

            Assert.Equal(1, warnings);
        }

        [Fact]
        public void Launch_Abi4_NoRestrict_NoNetworkWarning()
        {
            FakeLogger logger = new FakeLogger();
            LinuxLandlockSandbox sandbox = LinuxLandlockSandbox.CreateForTest(abi: 4, logger);

            sandbox.EmitNetworkDisclosureWarnings(restrictNetwork: false);

            Assert.DoesNotContain(logger.Messages, m =>
                m.Text.Contains("PARTIAL") || m.Text.Contains("FULL network"));
        }

        // ── CanEnforceNetwork ─────────────────────────────────────────────────

        [Theory]
        [InlineData(1, false)]
        [InlineData(2, false)]
        [InlineData(3, false)]
        [InlineData(4, true)]
        [InlineData(5, true)]
        [InlineData(6, true)]
        public void CanEnforceNetwork_MatchesNetAbiThreshold(int abi, bool expected)
        {
            LinuxLandlockSandbox sandbox = LinuxLandlockSandbox.CreateForTest(
                abi, NullLogger.Instance);
            Assert.Equal(expected, sandbox.CanEnforceNetwork);
        }
    }

    // ── Minimal logger capture ────────────────────────────────────────────────

    internal sealed class FakeLogEntry
    {
        public LogLevel Level { get; init; }
        public string Text { get; init; } = string.Empty;
    }

    internal sealed class FakeLogger : ILogger
    {
        public List<FakeLogEntry> Messages { get; } = new List<FakeLogEntry>();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            System.Exception? exception, System.Func<TState, System.Exception?, string> formatter)
        {
            Messages.Add(new FakeLogEntry
            {
                Level = logLevel,
                Text  = formatter(state, exception),
            });
        }
    }
}
