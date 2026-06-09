using System;
using System.IO;
using Hina.Builder.Init;
using Hina.PackageManager.Descriptor;

namespace Hina.Builder.Tests
{
    public sealed class DefaultsResolverTests : IDisposable
    {
        private readonly string _root;

        public DefaultsResolverTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "hina-defaults-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }

        [Theory]
        [InlineData("My Cool Game", "my-cool-game")]
        [InlineData("123Game", "app-123game")]
        [InlineData("Foo__Bar", "foo-bar")]
        public void Slugify_ProducesValidName(string input, string expected)
        {
            Assert.Equal(expected, DefaultsResolver.Slugify(input));
        }

        [Fact]
        public void Resolve_NoFiles_UsesFolderSlug()
        {
            ScaffoldDefaults d = DefaultsResolver.Resolve(new DirectoryInfo(_root));
            // Folder name is "hina-defaults-<guid>" → slug keeps it lowercase/dashed.
            Assert.StartsWith("hina-defaults-", d.Name);
            Assert.Equal("1.0.0", d.Version);
        }

        [Fact]
        public void Resolve_Csproj_PreFillsFromMetadata()
        {
            File.WriteAllText(Path.Combine(_root, "Game.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <AssemblyName>Neon Drift</AssemblyName>
                    <Version>3.1</Version>
                    <Authors>Pixel Co</Authors>
                  </PropertyGroup>
                </Project>
                """);

            ScaffoldDefaults d = DefaultsResolver.Resolve(new DirectoryInfo(_root));

            Assert.Equal("neon-drift", d.Name);       // slugified
            Assert.Equal("Neon Drift", d.DisplayName); // raw
            Assert.Equal("3.1.0", d.Version);          // normalised to SemVer
            Assert.Equal("Pixel Co", d.Publisher);
        }

        [Fact]
        public void Resolve_ExistingDescriptor_TakesPriorityOverMetadata()
        {
            // csproj says one thing...
            File.WriteAllText(Path.Combine(_root, "Game.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><AssemblyName>FromCsproj</AssemblyName><Version>9.9.9</Version></PropertyGroup>
                </Project>
                """);
            // ...but an existing hina.app.json wins (re-run = edit).
            AppDescriptor existing = new AppDescriptor
            {
                Name = "existing-app",
                DisplayName = "Existing App",
                Version = "4.5.6",
                Publisher = "Prev Publisher",
                BaseUrl = "https://prev.example/"
            };
            File.WriteAllText(Path.Combine(_root, "hina.app.json"), DescriptorParser.Serialize(existing));

            ScaffoldDefaults d = DefaultsResolver.Resolve(new DirectoryInfo(_root));

            Assert.Equal("existing-app", d.Name);
            Assert.Equal("4.5.6", d.Version);
            Assert.Equal("Prev Publisher", d.Publisher);
            Assert.Equal("https://prev.example/", d.BaseUrl);
            Assert.NotNull(d.Existing);
        }
    }
}
