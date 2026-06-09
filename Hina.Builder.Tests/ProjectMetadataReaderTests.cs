using System;
using System.IO;
using Hina.Builder.Init;

namespace Hina.Builder.Tests
{
    public sealed class ProjectMetadataReaderTests : IDisposable
    {
        private readonly string _root;

        public ProjectMetadataReaderTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "hina-meta-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }

        [Fact]
        public void Read_EmptyDir_IsEmpty()
        {
            Assert.True(ProjectMetadataReader.Read(new DirectoryInfo(_root)).IsEmpty);
        }

        [Fact]
        public void Read_Csproj_ExtractsFields()
        {
            File.WriteAllText(Path.Combine(_root, "MyGame.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <AssemblyName>SuperGame</AssemblyName>
                    <Version>2.3.4</Version>
                    <Authors>Acme Studios</Authors>
                    <Description>A fun game</Description>
                    <PackageProjectUrl>https://acme.example</PackageProjectUrl>
                  </PropertyGroup>
                </Project>
                """);

            ProjectMetadata m = ProjectMetadataReader.Read(new DirectoryInfo(_root));

            Assert.Equal("SuperGame", m.Name);
            Assert.Equal("2.3.4", m.Version);
            Assert.Equal("Acme Studios", m.Publisher);
            Assert.Equal("A fun game", m.Description);
            Assert.Equal("https://acme.example", m.Homepage);
        }

        [Fact]
        public void Read_PackageJson_ExtractsFields()
        {
            File.WriteAllText(Path.Combine(_root, "package.json"), """
                { "name": "my-app", "version": "1.0.0", "description": "Desc",
                  "author": "Jane Dev", "homepage": "https://jane.example" }
                """);

            ProjectMetadata m = ProjectMetadataReader.Read(new DirectoryInfo(_root));

            Assert.Equal("my-app", m.Name);
            Assert.Equal("1.0.0", m.Version);
            Assert.Equal("Jane Dev", m.Publisher);
            Assert.Equal("https://jane.example", m.Homepage);
        }

        [Fact]
        public void Read_Godot_ExtractsName()
        {
            File.WriteAllText(Path.Combine(_root, "project.godot"), """
                [application]
                config/name="My Godot Game"
                config/version="0.9.1"
                """);

            ProjectMetadata m = ProjectMetadataReader.Read(new DirectoryInfo(_root));

            Assert.Equal("My Godot Game", m.Name);
            Assert.Equal("0.9.1", m.Version);
        }
    }
}
