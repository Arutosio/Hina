using Hina.PackageManager.Platform.Windows;
using Xunit;

namespace Hina.PackageManager.Tests
{
    // A .lnk shortcut stores its launch command as a separate target path + argument
    // string, but InstallService hands every backend ONE opaque override string
    // (`"<hinaExe>" run <app> "<entry>"`). Splitting that back into (exe, args) for the
    // .lnk is pure string logic, so it is unit-tested here on every host; the COM .lnk
    // write itself is Windows-only (WindowsPlatformIntegrationTests, gated to Windows CI).
    public class WindowsLaunchOverrideTests
    {
        [Fact]
        public void Parse_QuotedExeWithSpaces_SplitsExeFromArgs()
        {
            WindowsLaunchOverride parsed = WindowsLaunchOverride.Parse("\"C:\\Program Files\\hina.exe\" run demo \"main\"");
            Assert.Equal("C:\\Program Files\\hina.exe", parsed.Exe);
            Assert.Equal("run demo \"main\"", parsed.Arguments);
        }

        [Fact]
        public void Parse_UnquotedExe_SplitsOnFirstWhitespace()
        {
            WindowsLaunchOverride parsed = WindowsLaunchOverride.Parse("hina run demo \"main\"");
            Assert.Equal("hina", parsed.Exe);
            Assert.Equal("run demo \"main\"", parsed.Arguments);
        }

        [Fact]
        public void Parse_ExeOnly_HasEmptyArguments()
        {
            WindowsLaunchOverride parsed = WindowsLaunchOverride.Parse("\"C:\\hina.exe\"");
            Assert.Equal("C:\\hina.exe", parsed.Exe);
            Assert.Equal("", parsed.Arguments);
        }
    }
}
