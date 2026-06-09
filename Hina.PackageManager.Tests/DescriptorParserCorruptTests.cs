using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hina.PackageManager.Descriptor;
using Xunit;

namespace Hina.PackageManager.Tests
{
    // `hina install <url>` pointing at something that isn't a descriptor (a web page, a
    // captive portal, a bucket listing) must fail with "not a valid Hina app descriptor",
    // not a raw JsonException ("'<' is an invalid start of a value...").
    public class DescriptorParserCorruptTests
    {
        [Theory]
        [InlineData("<html><body>It works!</body></html>")]
        [InlineData("not json")]
        [InlineData("")]
        [InlineData("null")]
        public void Parse_NonDescriptorContent_ThrowsActionableError(string content)
        {
            var ex = Assert.Throws<InvalidDataException>(() => DescriptorParser.Parse(content));
            Assert.Contains("descriptor", ex.Message, System.StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("<html><body>It works!</body></html>")]
        [InlineData("not json")]
        public async Task ReadAsync_NonDescriptorContent_ThrowsActionableError(string content)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
            var ex = await Assert.ThrowsAsync<InvalidDataException>(
                () => DescriptorParser.ReadAsync(stream, CancellationToken.None));
            Assert.Contains("descriptor", ex.Message, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Parse_ValidDescriptor_StillParses()
        {
            AppDescriptor d = DescriptorParser.Parse("""{ "name": "demo", "version": "1.0.0" }""");
            Assert.Equal("demo", d.Name);
        }
    }
}
