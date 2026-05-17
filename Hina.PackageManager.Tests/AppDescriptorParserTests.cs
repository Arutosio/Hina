using System.Linq;
using Hina.PackageManager.Descriptor;

namespace Hina.PackageManager.Tests
{
    public class AppDescriptorParserTests
    {
        [Fact]
        public void Parse_FullDescriptor_RoundTripsAllFields()
        {
            string json = """
            {
              "schemaVersion": 1,
              "name": "fooedit",
              "displayName": "FooEdit",
              "version": "2.3.1",
              "publisher": "Foo Software Ltd.",
              "description": "A fast text editor.",
              "homepage": "https://foo.example/",
              "license": "MIT",
              "icon": "icons/fooedit.png",
              "minHinaVersion": "0.4.0",
              "baseUrl": "https://cdn.foo.example/fooedit/",
              "channel": "stable",
              "publicKey": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
              "exec": { "windows": "bin\\fooedit.exe", "linux": "bin/fooedit", "macos": "FooEdit.app/Contents/MacOS/fooedit" },
              "entries": [
                { "id": "main", "name": "FooEdit", "exec": "bin/fooedit", "icon": "icons/fooedit.png", "categories": ["Development"], "terminal": false }
              ],
              "postInstall": [
                { "action": "addToPath", "name": "fooedit", "target": "bin/fooedit" },
                { "action": "registerMimeType", "mimeType": "application/x-fooedit", "extensions": [".foo"], "entryId": "main" },
                { "action": "registerUrlScheme", "scheme": "fooedit", "entryId": "main" },
                { "action": "installFont", "files": ["assets/Foo.ttf"] },
                { "action": "registerAutostart", "entryId": "main", "args": ["--minimized"] }
              ]
            }
            """;

            AppDescriptor d = DescriptorParser.Parse(json);

            Assert.Equal(1, d.SchemaVersion);
            Assert.Equal("fooedit", d.Name);
            Assert.Equal("FooEdit", d.DisplayName);
            Assert.Equal("2.3.1", d.Version);
            Assert.Equal("Foo Software Ltd.", d.Publisher);
            Assert.Equal("MIT", d.License);
            Assert.Equal("https://cdn.foo.example/fooedit/", d.BaseUrl);
            Assert.Equal("bin/fooedit", d.Exec.Linux);
            Assert.Single(d.Entries);
            Assert.Equal("main", d.Entries[0].Id);
            Assert.Equal(5, d.PostInstall.Count);

            Assert.IsType<AddToPathHook>(d.PostInstall[0]);
            Assert.IsType<MimeTypeHook>(d.PostInstall[1]);
            Assert.IsType<UrlSchemeHook>(d.PostInstall[2]);
            Assert.IsType<InstallFontHook>(d.PostInstall[3]);
            Assert.IsType<AutostartHook>(d.PostInstall[4]);

            MimeTypeHook mime = (MimeTypeHook)d.PostInstall[1];
            Assert.Equal("application/x-fooedit", mime.MimeType);
            Assert.Equal(new[] { ".foo" }, mime.Extensions);
            Assert.Equal("main", mime.EntryId);

            AutostartHook autostart = (AutostartHook)d.PostInstall[4];
            Assert.Equal(new[] { "--minimized" }, autostart.Args);
        }

        [Fact]
        public void Serialize_ThenParse_PreservesHookDiscriminators()
        {
            AppDescriptor original = new AppDescriptor
            {
                Name = "demo",
                DisplayName = "Demo",
                Version = "1.0.0",
                Publisher = "Acme",
                BaseUrl = "https://example.com/",
                PublicKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                Exec = new ExecMap { Linux = "bin/demo" },
                Entries = { new ShellEntry { Id = "main", Name = "Demo", Exec = "bin/demo" } },
                PostInstall =
                {
                    new AddToPathHook { Name = "demo", Target = "bin/demo" },
                    new InstallFontHook { Files = { "fonts/Demo.ttf" } }
                }
            };

            string json = DescriptorParser.Serialize(original);
            AppDescriptor parsed = DescriptorParser.Parse(json);

            Assert.Equal(2, parsed.PostInstall.Count);
            Assert.IsType<AddToPathHook>(parsed.PostInstall[0]);
            Assert.IsType<InstallFontHook>(parsed.PostInstall[1]);
        }

        [Fact]
        public void CanonicalBytes_AreStableAcrossInstances_AndIgnoreSignature()
        {
            AppDescriptor a = new AppDescriptor
            {
                Name = "demo",
                DisplayName = "Demo",
                Version = "1.0.0",
                Publisher = "Acme",
                BaseUrl = "https://example.com/",
                PublicKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                Exec = new ExecMap { Linux = "bin/demo" }
            };

            AppDescriptor b = DescriptorParser.Parse(DescriptorParser.Serialize(a));
            b.DescriptorSignature = new DescriptorSignature
            {
                Algorithm = "ed25519",
                Signature = "fakesig",
                PublicKey = a.PublicKey
            };

            byte[] bytesA = DescriptorParser.GetCanonicalBytes(a);
            byte[] bytesB = DescriptorParser.GetCanonicalBytes(b);

            Assert.Equal(bytesA, bytesB);
        }
    }

    public class DescriptorValidatorTests
    {
        private static AppDescriptor ValidDescriptor() => new AppDescriptor
        {
            SchemaVersion = 1,
            Name = "fooedit",
            DisplayName = "FooEdit",
            Version = "1.0.0",
            Publisher = "Foo",
            BaseUrl = "https://example.com/foo/",
            PublicKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            Exec = new ExecMap { Linux = "bin/foo" },
            Entries = { new ShellEntry { Id = "main", Name = "Foo", Exec = "bin/foo" } },
            PostInstall = { new AddToPathHook { Name = "foo", Target = "bin/foo" } }
        };

        [Fact]
        public void Validate_FullValidDescriptor_HasNoErrors()
        {
            ValidationResult r = DescriptorValidator.Validate(ValidDescriptor());
            Assert.True(r.IsValid, string.Join("; ", r.Errors));
        }

        [Theory]
        [InlineData("Foo")]
        [InlineData("1foo")]
        [InlineData("foo bar")]
        [InlineData("")]
        public void Validate_RejectsInvalidName(string name)
        {
            AppDescriptor d = ValidDescriptor();
            d.Name = name;
            ValidationResult r = DescriptorValidator.Validate(d);
            Assert.Contains(r.Errors, e => e.Contains("name"));
        }

        [Fact]
        public void Validate_RejectsHttpBaseUrl_WhenInsecureDisallowed()
        {
            AppDescriptor d = ValidDescriptor();
            d.BaseUrl = "http://example.com/foo/";
            ValidationResult r = DescriptorValidator.Validate(d);
            Assert.Contains(r.Errors, e => e.Contains("baseUrl") && e.Contains("HTTPS"));
        }

        [Fact]
        public void Validate_AllowsHttpBaseUrl_WhenInsecureAllowed()
        {
            AppDescriptor d = ValidDescriptor();
            d.BaseUrl = "http://example.com/foo/";
            ValidationResult r = DescriptorValidator.Validate(d, new ValidationContext { AllowInsecure = true });
            Assert.True(r.IsValid, string.Join("; ", r.Errors));
        }

        [Fact]
        public void Validate_RejectsAbsoluteExecPath()
        {
            AppDescriptor d = ValidDescriptor();
            d.Exec.Linux = "/usr/local/bin/foo";
            ValidationResult r = DescriptorValidator.Validate(d);
            Assert.Contains(r.Errors, e => e.Contains("relative"));
        }

        [Fact]
        public void Validate_RejectsParentTraversalInFontPath()
        {
            AppDescriptor d = ValidDescriptor();
            d.PostInstall = new() { new InstallFontHook { Files = { "../etc/passwd" } } };
            ValidationResult r = DescriptorValidator.Validate(d);
            Assert.Contains(r.Errors, e => e.Contains(".."));
        }

        [Fact]
        public void Validate_RejectsHookEntryIdReferencingMissingEntry()
        {
            AppDescriptor d = ValidDescriptor();
            d.PostInstall.Add(new UrlSchemeHook { Scheme = "demo", EntryId = "ghost" });
            ValidationResult r = DescriptorValidator.Validate(d);
            Assert.Contains(r.Errors, e => e.Contains("ghost"));
        }

        [Fact]
        public void Validate_RejectsDuplicateEntryIds()
        {
            AppDescriptor d = ValidDescriptor();
            d.Entries.Add(new ShellEntry { Id = "main", Name = "Other", Exec = "bin/other" });
            ValidationResult r = DescriptorValidator.Validate(d);
            Assert.Contains(r.Errors, e => e.Contains("duplicated"));
        }

        [Fact]
        public void Validate_RejectsNonBase64PublicKey()
        {
            AppDescriptor d = ValidDescriptor();
            d.PublicKey = "not-base64-!@#$";
            ValidationResult r = DescriptorValidator.Validate(d);
            Assert.Contains(r.Errors, e => e.Contains("base64") || e.Contains("publicKey"));
        }

        [Fact]
        public void Validate_RejectsPublicKeyWithWrongLength()
        {
            AppDescriptor d = ValidDescriptor();
            d.PublicKey = "AAAA";
            ValidationResult r = DescriptorValidator.Validate(d);
            Assert.Contains(r.Errors, e => e.Contains("32 bytes"));
        }

        [Fact]
        public void EnsureValid_ThrowsWithAggregatedErrors()
        {
            AppDescriptor d = ValidDescriptor();
            d.Name = "Bad Name";
            d.Version = "notsemver";

            DescriptorValidationException ex = Assert.Throws<DescriptorValidationException>(
                () => DescriptorValidator.Validate(d).EnsureValid());

            Assert.Equal(2, ex.Errors.Count);
        }
    }
}
