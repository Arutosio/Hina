namespace Hina.Host.Tests
{
    public class RoutingTests
    {
        [Fact]
        public void ExtractApp_NoAppsConfigured_ReturnsDefault()
        {
            var opt = new HostOptions();
            Assert.Equal("default", Routing.ExtractApp("/anything/manifest.json", opt));
        }

        [Fact]
        public void ExtractApp_KnownPrefix_ReturnsAppName()
        {
            var opt = new HostOptions();
            opt.Apps["gameA"] = "/srv/gameA";
            Assert.Equal("gameA", Routing.ExtractApp("/gameA/manifest.json", opt));
        }

        [Fact]
        public void ExtractApp_KnownPrefix_IsCaseInsensitive()
        {
            var opt = new HostOptions();
            opt.Apps["gameA"] = "/srv/gameA";
            Assert.Equal("GAMEA", Routing.ExtractApp("/GAMEA/manifest.json", opt));
        }

        [Fact]
        public void ExtractApp_UnknownPrefix_ReturnsUnknown()
        {
            var opt = new HostOptions();
            opt.Apps["gameA"] = "/srv/gameA";
            Assert.Equal("unknown", Routing.ExtractApp("/other/manifest.json", opt));
        }

        [Fact]
        public void ExtractApp_RootPath_WithApps_ReturnsUnknown()
        {
            var opt = new HostOptions();
            opt.Apps["gameA"] = "/srv/gameA";
            Assert.Equal("unknown", Routing.ExtractApp("/", opt));
        }
    }
}
