using System.Collections.Generic;
using System.Linq;
using Hina.PackageManager.Sandbox;
using Xunit;

namespace Hina.PackageManager.Tests
{
    // Guards the curated list of implicit system-runtime paths that a Linux
    // Landlock sandbox must grant so dynamically-linked apps can load their
    // loader + libc + system libs. Without these, only fully self-contained
    // (static/AOT) binaries run; everything else EACCES at startup.
    public class LinuxSystemRuntimePathsTests
    {
        private static IReadOnlyList<ResolvedFsRule> Paths => LinuxLandlockSandbox.SystemRuntimePaths;

        [Theory]
        [InlineData("/usr")]
        [InlineData("/lib")]
        [InlineData("/lib64")]
        [InlineData("/bin")]
        [InlineData("/sbin")]
        [InlineData("/etc")]
        public void GrantsReadOnSystemLibraryDirs(string path)
        {
            ResolvedFsRule? rule = Paths.FirstOrDefault(r => r.Path == path);
            Assert.NotNull(rule);
            Assert.False(rule!.CanWrite); // loader/libs are read+exec only, never writable.
        }

        [Theory]
        [InlineData("/dev/null")]
        [InlineData("/dev/zero")]
        [InlineData("/dev/full")]
        [InlineData("/dev/random")]
        [InlineData("/dev/urandom")]
        [InlineData("/dev/tty")]
        public void GrantsReadWriteOnCommonDeviceNodes(string path)
        {
            ResolvedFsRule? rule = Paths.FirstOrDefault(r => r.Path == path);
            Assert.NotNull(rule);
            Assert.True(rule!.CanWrite); // device nodes are written to (e.g. /dev/null).
        }

        [Fact]
        public void DoesNotGrantAllOfDev()
        {
            // Granting all of /dev would expose block devices, /dev/mem, etc.
            // Only the specific safe nodes above are granted.
            Assert.DoesNotContain(Paths, r => r.Path == "/dev");
        }

        [Fact]
        public void DoesNotGrantSensitiveSecretDirs()
        {
            // DAC still protects /etc/shadow as non-root, but the curated list
            // must never broaden to whole-home or root.
            Assert.DoesNotContain(Paths, r => r.Path == "/" || r.Path == "/root" || r.Path == "/home");
        }
    }
}
