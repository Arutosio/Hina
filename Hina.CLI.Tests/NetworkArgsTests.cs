using System;
using Hina.CLI.Commands;
using Hina.PackageManager.Install;
using Xunit;

namespace Hina.CLI.Tests
{
    // A network-tuning flag passed with a bad value used to be silently dropped and the engine
    // default used instead — `--retries 0`, `--retries -5`, `--retries abc` all looked honored
    // while doing nothing. NetworkArgs now fails loudly when a flag is present but not a
    // positive integer; an absent flag still falls back to the default.
    public sealed class NetworkArgsTests
    {
        [Fact]
        public void FromArgs_FlagAbsent_UsesDefault()
        {
            NetworkOptions defaults = new NetworkOptions();
            NetworkOptions opts = NetworkArgs.FromArgs(new[] { "install", "https://e.com/a.json" });
            Assert.Equal(defaults.MaxRetries, opts.MaxRetries);
        }

        [Fact]
        public void FromArgs_ValidRetries_IsHonored()
        {
            NetworkOptions opts = NetworkArgs.FromArgs(new[] { "install", "https://e.com/a.json", "--retries", "5" });
            Assert.Equal(5, opts.MaxRetries);
        }

        [Theory]
        [InlineData("abc")]
        [InlineData("0")]
        [InlineData("-5")]
        public void FromArgs_InvalidRetries_Throws(string value)
        {
            Assert.Throws<FormatException>(() =>
                NetworkArgs.FromArgs(new[] { "install", "https://e.com/a.json", "--retries", value }));
        }
    }
}
