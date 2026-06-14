using System;
using System.IO;
using System.Runtime.InteropServices;
using Hina.Core.IO;
using Xunit;

namespace Hina.Core.Tests
{
    public class PathUtilsTests
    {
        private static string Root => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? @"C:\hina\app" : "/hina/app";

        [Fact]
        public void ToOsPath_NormalRelativePath_StaysInsideRoot()
        {
            string result = PathUtils.ToOsPath(Root, "bin/tool");
            Assert.StartsWith(Path.GetFullPath(Root), result);
            Assert.EndsWith(Path.Combine("bin", "tool"), result);
        }

        [Fact]
        public void ToOsPath_RootItself_IsAllowed()
        {
            // An empty/"." relative path resolves to the root — allowed.
            string result = PathUtils.ToOsPath(Root, ".");
            Assert.Equal(Path.GetFullPath(Root), result);
        }

        [Theory]
        [InlineData("../../etc/passwd")]
        [InlineData("../sibling/x")]
        [InlineData("a/../../b")]
        public void ToOsPath_TraversalAttempt_Throws(string evil)
        {
            Assert.Throws<InvalidDataException>(() => PathUtils.ToOsPath(Root, evil));
        }

        [Fact]
        public void ToOsPath_AbsolutePath_Throws()
        {
            string abs = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? @"C:\Windows\system32\x" : "/etc/cron.d/x";
            Assert.Throws<InvalidDataException>(() => PathUtils.ToOsPath(Root, abs));
        }

        [Fact]
        public void ToOsPath_PrefixSiblingDir_Throws()
        {
            // "/hina/app-evil" shares the "/hina/app" string prefix but is NOT inside root.
            Assert.Throws<InvalidDataException>(() => PathUtils.ToOsPath(Root, "../app-evil/x"));
        }

        [Fact]
        public void ToOsPath_NullManifestPath_ThrowsArgumentNullException()
        {
            // A JSON manifest with "path": null deserializes to null; must be rejected cleanly,
            // not with an opaque NullReferenceException from manifestPath.Replace().
            var ex = Assert.Throws<ArgumentNullException>(() => PathUtils.ToOsPath(Root, null!));
            Assert.Equal("manifestPath", ex.ParamName);
        }

        [Fact]
        public void ToOsPath_NullManifestPath_ExceptionIsNotNullReferenceException()
        {
            // Regression guard: the old code threw NRE; ensure the exception type is exactly
            // ArgumentNullException (a subclass check would pass for NRE too, so use type equality).
            Exception? caught = null;
            try { PathUtils.ToOsPath(Root, null!); }
            catch (Exception e) { caught = e; }

            Assert.NotNull(caught);
            Assert.Equal(typeof(ArgumentNullException), caught!.GetType());
        }
    }
}
