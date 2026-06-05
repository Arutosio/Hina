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
    }
}
