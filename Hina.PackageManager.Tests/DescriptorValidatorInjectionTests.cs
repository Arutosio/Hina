using System.Linq;
using Hina.Core.Crypto;
using Hina.PackageManager.Descriptor;
using Xunit;

namespace Hina.PackageManager.Tests
{
    // Sec HIGH: descriptor fields that are written into shell-integration artifacts (.desktop keys,
    // plist, registry) must be charset/control-char constrained so a signed-but-hostile descriptor
    // can't inject extra keys (e.g. an autostart Exec= line).
    public class DescriptorValidatorInjectionTests
    {
        private static AppDescriptor Base()
        {
            (_, string pub) = KeyGenerator.GenerateEd25519();
            return new AppDescriptor
            {
                SchemaVersion = 1,
                Name = "demo",
                DisplayName = "Demo",
                Version = "1.0.0",
                Publisher = "Acme",
                Channel = "stable",
                BaseUrl = "https://example.com/demo/",
                PublicKey = pub,
                Exec = new ExecMap { Linux = "bin/demo" },
                Entries = { new ShellEntry { Id = "main", Name = "Demo", Exec = "bin/demo" } }
            };
        }

        [Fact]
        public void Categories_WithInjectedDesktopKey_IsRejected()
        {
            AppDescriptor d = Base();
            d.Entries[0].Categories.Add("Utility\nExec=/bin/sh -c evil");
            Assert.False(DescriptorValidator.Validate(d).IsValid);
        }

        [Theory]
        [InlineData("text/plain\nExec=/bin/sh")]
        [InlineData("not a mime")]
        [InlineData("text/")]
        public void MimeType_Invalid_IsRejected(string mime)
        {
            AppDescriptor d = Base();
            d.PostInstall.Add(new MimeTypeHook { MimeType = mime, Extensions = { "txt" }, EntryId = "main" });
            Assert.False(DescriptorValidator.Validate(d).IsValid);
        }

        [Fact]
        public void MimeType_Valid_IsAccepted()
        {
            AppDescriptor d = Base();
            d.PostInstall.Add(new MimeTypeHook { MimeType = "application/x-demo", Extensions = { "dmo" }, EntryId = "main" });
            Assert.True(DescriptorValidator.Validate(d).IsValid, string.Join("; ", DescriptorValidator.Validate(d).Errors));
        }

        [Theory]
        [InlineData("demo\nExec=evil")]
        [InlineData("Demo")]   // uppercase not allowed in a URL scheme
        [InlineData("1demo")]  // must start with a letter
        public void Scheme_Invalid_IsRejected(string scheme)
        {
            AppDescriptor d = Base();
            d.PostInstall.Add(new UrlSchemeHook { Scheme = scheme, EntryId = "main" });
            Assert.False(DescriptorValidator.Validate(d).IsValid);
        }

        [Fact]
        public void AutostartArgs_WithControlChars_IsRejected()
        {
            AppDescriptor d = Base();
            d.PostInstall.Add(new AutostartHook { EntryId = "main", Args = new System.Collections.Generic.List<string> { "--ok", "bad\nExec=evil" } });
            Assert.False(DescriptorValidator.Validate(d).IsValid);
        }

        [Fact]
        public void DisplayNameWithNewline_IsRejected()
        {
            AppDescriptor d = Base();
            d.DisplayName = "Demo\nExec=evil";
            Assert.False(DescriptorValidator.Validate(d).IsValid);
        }
    }
}
