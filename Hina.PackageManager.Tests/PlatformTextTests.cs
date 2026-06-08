using Hina.PackageManager.Platform;
using Xunit;

namespace Hina.PackageManager.Tests
{
    public class PlatformTextTests
    {
        [Fact]
        public void StripControl_RemovesNewlinesAndControlChars()
        {
            // Control chars (newline especially) must go so a value can't inject extra
            // .desktop Exec lines / shell-script lines at a write site.
            Assert.Equal("abc", PlatformText.StripControl("a\nb\tc"));
            Assert.Equal("hinarun", PlatformText.StripControl("hina\r\nrun"));
        }

        [Fact]
        public void StripControl_NoControlChars_ReturnsSameContent()
        {
            Assert.Equal("/usr/bin/app --flag", PlatformText.StripControl("/usr/bin/app --flag"));
        }
    }
}
