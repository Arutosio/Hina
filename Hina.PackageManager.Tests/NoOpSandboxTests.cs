using System.Collections.Generic;
using System.Runtime.InteropServices;
using Hina.PackageManager.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hina.PackageManager.Tests
{
    public class NoOpSandboxTests
    {
        private static readonly SandboxPlan EmptyPlan = new SandboxPlan(false, new List<ResolvedFsRule>());

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        [Fact]
        public void IsSupported_IsFalse()
        {
            Assert.False(new NoOpSandbox(NullLogger.Instance).IsSupported);
        }

        [Fact]
        public void Launch_RunsProcess_ReturnsExitCode()
        {
            if (IsWindows) return; // /bin/sh is Unix-only; NoOp's spawn is the same code path on every OS.
            NoOpSandbox sandbox = new NoOpSandbox(NullLogger.Instance);
            int code = sandbox.Launch("/bin/sh", new List<string> { "-c", "exit 7" }, EmptyPlan, default);
            Assert.Equal(7, code);
        }

        [Fact]
        public void Launch_PassesArgs()
        {
            if (IsWindows) return; // /bin/sh is Unix-only.
            NoOpSandbox sandbox = new NoOpSandbox(NullLogger.Instance);
            // sh -c 'exit $#' arg0 a b c  -> exit code = number of extra args after the -c command name
            int code = sandbox.Launch("/bin/sh", new List<string> { "-c", "exit $#", "x", "a", "b" }, EmptyPlan, default);
            Assert.Equal(2, code);
        }
    }
}
