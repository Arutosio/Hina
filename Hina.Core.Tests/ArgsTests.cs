using System.Collections.Generic;
using Hina.Core.Cli;
using Xunit;

namespace Hina.Core.Tests
{
    // Unit tests for Hina.Core.Cli.Args helpers.
    // Covers BUG-024: FirstUnknownFlag must not report a flag-value that starts with '-'
    // as an unknown flag when valuedFlags is provided.
    public class ArgsTests
    {
        // -----------------------------------------------------------------------
        // HasFlag
        // -----------------------------------------------------------------------

        [Fact]
        public void HasFlag_PresentFlag_ReturnsTrue()
        {
            string[] args = { "--verbose", "--output", "file.txt" };
            Assert.True(Args.HasFlag(args, "--verbose"));
        }

        [Fact]
        public void HasFlag_AbsentFlag_ReturnsFalse()
        {
            string[] args = { "--verbose" };
            Assert.False(Args.HasFlag(args, "--output"));
        }

        [Fact]
        public void HasFlag_IsCaseInsensitive()
        {
            string[] args = { "--Verbose" };
            Assert.True(Args.HasFlag(args, "--verbose"));
        }

        // -----------------------------------------------------------------------
        // GetValue
        // -----------------------------------------------------------------------

        [Fact]
        public void GetValue_PresentFlag_ReturnsNextToken()
        {
            string[] args = { "--name", "alice" };
            Assert.Equal("alice", Args.GetValue(args, "--name"));
        }

        [Fact]
        public void GetValue_FlagAtLastPosition_ReturnsNull()
        {
            string[] args = { "--name" };
            Assert.Null(Args.GetValue(args, "--name"));
        }

        [Fact]
        public void GetValue_AbsentFlag_ReturnsNull()
        {
            string[] args = { "--output", "file.txt" };
            Assert.Null(Args.GetValue(args, "--name"));
        }

        // -----------------------------------------------------------------------
        // FirstPositional
        // -----------------------------------------------------------------------

        [Fact]
        public void FirstPositional_NoValuedFlags_ReturnsFirstNonFlag()
        {
            string[] args = { "--verbose", "myapp" };
            HashSet<string> noValued = new HashSet<string>();
            Assert.Equal("myapp", Args.FirstPositional(args, noValued));
        }

        [Fact]
        public void FirstPositional_ValuedFlagBeforePositional_SkipsValue()
        {
            // `--retries 3 myapp` — "3" is the value of --retries, not the positional.
            string[] args = { "--retries", "3", "myapp" };
            HashSet<string> valued = new HashSet<string> { "--retries" };
            Assert.Equal("myapp", Args.FirstPositional(args, valued));
        }

        [Fact]
        public void FirstPositional_NoPositional_ReturnsNull()
        {
            string[] args = { "--verbose" };
            HashSet<string> noValued = new HashSet<string>();
            Assert.Null(Args.FirstPositional(args, noValued));
        }

        // -----------------------------------------------------------------------
        // FirstUnknownFlag — no valuedFlags (legacy / backward-compat path)
        // -----------------------------------------------------------------------

        [Fact]
        public void FirstUnknownFlag_AllKnown_ReturnsNull()
        {
            string[] args = { "--verbose", "--output", "file.txt" };
            HashSet<string> known = new HashSet<string> { "--verbose", "--output" };
            Assert.Null(Args.FirstUnknownFlag(args, known));
        }

        [Fact]
        public void FirstUnknownFlag_OneUnknown_ReturnsIt()
        {
            string[] args = { "--verbose", "--typo" };
            HashSet<string> known = new HashSet<string> { "--verbose" };
            Assert.Equal("--typo", Args.FirstUnknownFlag(args, known));
        }

        [Fact]
        public void FirstUnknownFlag_StartIndex_SkipsEarlierTokens()
        {
            // Token at index 0 is unknown but startIndex=1 skips it.
            string[] args = { "--unknown", "--verbose" };
            HashSet<string> known = new HashSet<string> { "--verbose" };
            Assert.Null(Args.FirstUnknownFlag(args, known, startIndex: 1));
        }

        [Fact]
        public void FirstUnknownFlag_NonFlagTokens_AreIgnored()
        {
            string[] args = { "positional", "--verbose" };
            HashSet<string> known = new HashSet<string> { "--verbose" };
            Assert.Null(Args.FirstUnknownFlag(args, known));
        }

        // -----------------------------------------------------------------------
        // BUG-024 — FirstUnknownFlag with valuedFlags must skip value tokens
        // -----------------------------------------------------------------------

        [Fact]
        public void BUG024_ValueStartingWithDash_IsNotReportedAsUnknown()
        {
            // `--name -x`: --name is a known valued flag; '-x' is its value, not a flag.
            string[] args = { "--name", "-x" };
            HashSet<string> known = new HashSet<string> { "--name" };
            HashSet<string> valued = new HashSet<string> { "--name" };
            Assert.Null(Args.FirstUnknownFlag(args, known, valuedFlags: valued));
        }

        [Fact]
        public void BUG024_ValueStartingWithDoubleDash_IsNotReportedAsUnknown()
        {
            // `--output --some-value`: value starts with '--'.
            string[] args = { "--output", "--some-value" };
            HashSet<string> known = new HashSet<string> { "--output" };
            HashSet<string> valued = new HashSet<string> { "--output" };
            Assert.Null(Args.FirstUnknownFlag(args, known, valuedFlags: valued));
        }

        [Fact]
        public void BUG024_TrueUnknownFlagAfterValuedFlag_IsStillReported()
        {
            // `--name -x --typo`: '-x' is the value of --name (skipped), '--typo' is genuinely unknown.
            string[] args = { "--name", "-x", "--typo" };
            HashSet<string> known = new HashSet<string> { "--name" };
            HashSet<string> valued = new HashSet<string> { "--name" };
            Assert.Equal("--typo", Args.FirstUnknownFlag(args, known, valuedFlags: valued));
        }

        [Fact]
        public void BUG024_MultipleValuedFlags_AllValueTokensSkipped()
        {
            // `--name -x --path -/tmp/dir`: both values start with '-'; neither is unknown.
            string[] args = { "--name", "-x", "--path", "-/tmp/dir" };
            HashSet<string> known = new HashSet<string> { "--name", "--path" };
            HashSet<string> valued = new HashSet<string> { "--name", "--path" };
            Assert.Null(Args.FirstUnknownFlag(args, known, valuedFlags: valued));
        }

        [Fact]
        public void BUG024_ValuedFlagWithNormalValue_Works()
        {
            // Valued flag whose value does NOT start with '-' — normal case still works.
            string[] args = { "--name", "alice", "--extra" };
            HashSet<string> known = new HashSet<string> { "--name" };
            HashSet<string> valued = new HashSet<string> { "--name" };
            Assert.Equal("--extra", Args.FirstUnknownFlag(args, known, valuedFlags: valued));
        }

        [Fact]
        public void BUG024_ValuedFlagAtEndWithNoValue_DoesNotThrow()
        {
            // `--name` with no following token: valued flag at end of args, i+1 >= Length.
            string[] args = { "--name" };
            HashSet<string> known = new HashSet<string> { "--name" };
            HashSet<string> valued = new HashSet<string> { "--name" };
            // No value to skip; should not throw and should return null (no unknown flags).
            Assert.Null(Args.FirstUnknownFlag(args, known, valuedFlags: valued));
        }

        [Fact]
        public void BUG024_NullValuedFlags_PreservesLegacyBehavior()
        {
            // Passing valuedFlags: null keeps original behavior — '-x' IS reported as unknown.
            string[] args = { "--name", "-x" };
            HashSet<string> known = new HashSet<string> { "--name" };
            Assert.Equal("-x", Args.FirstUnknownFlag(args, known, valuedFlags: null));
        }

        [Fact]
        public void BUG024_OmittedValuedFlags_PreservesLegacyBehavior()
        {
            // Omitting valuedFlags entirely (default null) keeps original behavior.
            string[] args = { "--name", "-x" };
            HashSet<string> known = new HashSet<string> { "--name" };
            Assert.Equal("-x", Args.FirstUnknownFlag(args, known));
        }

        [Fact]
        public void BUG024_StartIndexCombinedWithValuedFlags_SkipsCorrectly()
        {
            // startIndex=1 skips index 0 ("subcommand"); --name at index 1 has value '-x' at index 2.
            string[] args = { "subcommand", "--name", "-x", "--typo" };
            HashSet<string> known = new HashSet<string> { "--name" };
            HashSet<string> valued = new HashSet<string> { "--name" };
            Assert.Equal("--typo", Args.FirstUnknownFlag(args, known, startIndex: 1, valuedFlags: valued));
        }
    }
}
