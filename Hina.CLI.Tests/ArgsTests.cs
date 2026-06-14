using Hina.CLI;
using Xunit;

namespace Hina.CLI.Tests
{
    // Regression coverage for Args.FirstPositional, which must skip both a valued flag
    // and the value token that follows it. A flag placed before the positional argument
    // (e.g. `hina install --retries 3 <url>`) previously returned the value as the positional.
    public sealed class ArgsTests
    {
        [Fact]
        public void FirstPositional_SkipsValuedFlagValue()
        {
            string[] args = { "install", "--retries", "3", "https://example.com/app.json" };
            Assert.Equal("https://example.com/app.json", Args.FirstPositional(args, startIndex: 1));
        }

        [Fact]
        public void FirstPositional_SkipsMultipleValuedFlags()
        {
            string[] args = { "install", "--dir", "/tmp/x", "--jobs", "4", "myapp" };
            Assert.Equal("myapp", Args.FirstPositional(args, startIndex: 1));
        }

        [Fact]
        public void FirstPositional_PositionalBeforeFlags_StillWorks()
        {
            string[] args = { "update", "myapp", "--jobs", "4" };
            Assert.Equal("myapp", Args.FirstPositional(args, startIndex: 1));
        }

        [Fact]
        public void FirstPositional_BooleanFlagDoesNotConsumeNext()
        {
            // --force takes no value, so the following token IS the positional.
            string[] args = { "update", "--force", "myapp" };
            Assert.Equal("myapp", Args.FirstPositional(args, startIndex: 1));
        }

        [Fact]
        public void FirstPositional_NoPositional_ReturnsNull()
        {
            string[] args = { "update", "--jobs", "4" };
            Assert.Null(Args.FirstPositional(args, startIndex: 1));
        }

        [Fact]
        public void FirstUnknownFlag_DetectsTypo()
        {
            // `--allow-insecue` is a typo of `--allow-insecure`; it must be surfaced, not
            // silently ignored (a dropped `--allow-insecure` would change security behavior).
            var known = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            {
                "--allow-insecure"
            };
            string[] args = { "install", "https://example.com/a.json", "--allow-insecue" };
            Assert.Equal("--allow-insecue", Args.FirstUnknownFlag(args, known, startIndex: 1));
        }

        [Fact]
        public void FirstUnknownFlag_AllKnown_ReturnsNull()
        {
            var known = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            {
                "--allow-insecure", "--retries"
            };
            string[] args = { "install", "https://example.com/a.json", "--allow-insecure", "--retries", "3" };
            Assert.Null(Args.FirstUnknownFlag(args, known, startIndex: 1));
        }

        // BUG-025: KnownFlags in PermsCommand used StringComparer.Ordinal, but HasFlag/GetValue
        // use OrdinalIgnoreCase. A mixed-case variant like `--Grant` was therefore rejected by
        // FirstUnknownFlag before HasFlag/GetValue could recognise it. After the fix, the KnownFlags
        // set is also OrdinalIgnoreCase, so case variants are treated consistently throughout.

        [Theory]
        [InlineData("--grant")]
        [InlineData("--Grant")]
        [InlineData("--GRANT")]
        [InlineData("--revoke")]
        [InlineData("--Revoke")]
        [InlineData("--REVOKE")]
        public void FirstUnknownFlag_PermsKnownFlags_CaseVariants_AreNotUnknown(string flag)
        {
            // Reproduce the exact set PermsCommand now uses (OrdinalIgnoreCase).
            var knownFlags = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            {
                "--grant", "--revoke"
            };
            string[] args = { "perms", "demo", flag, "/tmp/x" };
            Assert.Null(Args.FirstUnknownFlag(args, knownFlags, startIndex: 1));
        }

        [Fact]
        public void FirstUnknownFlag_PermsKnownFlags_OrdinalWouldHaveFailed_UpperCase()
        {
            // Document the OLD (broken) behaviour: Ordinal would have surfaced "--Grant" as unknown.
            var ordinalSet = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal)
            {
                "--grant", "--revoke"
            };
            string[] args = { "perms", "demo", "--Grant", "/tmp/x" };
            Assert.Equal("--Grant", Args.FirstUnknownFlag(args, ordinalSet, startIndex: 1));
        }

        [Fact]
        public void HasFlag_CaseInsensitive_RecognisesMixedCase()
        {
            // HasFlag uses OrdinalIgnoreCase in Core.Cli.Args — verify it accepts mixed-case
            // perms flags so the BUG-025 fix closes the full round-trip (detect + read).
            string[] args = { "perms", "demo", "--Grant", "/tmp/x" };
            Assert.True(Args.HasFlag(args, "--grant"));
        }

        [Fact]
        public void GetValue_CaseInsensitive_ReturnsMixedCaseFlagValue()
        {
            // GetValue also uses OrdinalIgnoreCase — `--Grant /tmp/x` must yield "/tmp/x".
            string[] args = { "perms", "demo", "--Grant", "/tmp/x" };
            Assert.Equal("/tmp/x", Args.GetValue(args, "--grant"));
        }
    }
}
