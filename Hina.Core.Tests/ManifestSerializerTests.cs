using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hina.Core.Manifest;
using Xunit;

namespace Hina.Core.Tests
{
    public class ManifestSerializerTests
    {
        [Fact]
        public async Task WriteAsync_ReadAsync_RoundTrip()
        {
            string tempDir = CreateTempDir();
            try
            {
                string path = Path.Combine(tempDir, "manifest.json");
                var manifest = new Manifest.Manifest
                {
                    Version = "1.2.3",
                    BaseUrl = "https://example.com/",
                    Files = new List<ManifestFile>
                    {
                        new ManifestFile
                        {
                            Path = "bin/app.exe",
                            Size = 1024,
                            FileHash = "sha256:aabbcc",
                            ChunkSize = 4,
                            Chunks = new List<ManifestChunk>
                            {
                                new ManifestChunk { Index = 0, Weak = 12345, Strong = "sha256:ddeeff", Size = 4 }
                            }
                        }
                    }
                };

                await ManifestSerializer.WriteAsync(manifest, path, CancellationToken.None);
                Assert.True(File.Exists(path));

                Manifest.Manifest loaded = await ManifestSerializer.ReadAsync(path, CancellationToken.None);

                Assert.Equal("1.2.3", loaded.Version);
                Assert.Equal("https://example.com/", loaded.BaseUrl);
                Assert.Single(loaded.Files);
                Assert.Equal("bin/app.exe", loaded.Files[0].Path);
                Assert.Equal(1024, loaded.Files[0].Size);
                Assert.Equal("sha256:aabbcc", loaded.Files[0].FileHash);
                Assert.Single(loaded.Files[0].Chunks);
                Assert.Equal(12345u, loaded.Files[0].Chunks[0].Weak);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task WriteAsync_ProducesValidJson()
        {
            string tempDir = CreateTempDir();
            try
            {
                string path = Path.Combine(tempDir, "manifest.json");
                var manifest = new Manifest.Manifest
                {
                    Version = "2.0.0",
                    BaseUrl = "http://localhost/"
                };

                await ManifestSerializer.WriteAsync(manifest, path, CancellationToken.None);
                string json = File.ReadAllText(path);

                // Should be indented JSON
                Assert.Contains("\"Version\"", json);
                Assert.Contains("\"2.0.0\"", json);
                Assert.Contains("\n", json); // indented
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task ReadAsync_EmptyManifest_ReturnsDefaults()
        {
            string tempDir = CreateTempDir();
            try
            {
                string path = Path.Combine(tempDir, "manifest.json");
                File.WriteAllText(path, "{}");

                Manifest.Manifest loaded = await ManifestSerializer.ReadAsync(path, CancellationToken.None);

                Assert.NotNull(loaded);
                Assert.Empty(loaded.Files);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task WriteAsync_ReadAsync_PreservesSignature()
        {
            string tempDir = CreateTempDir();
            try
            {
                string path = Path.Combine(tempDir, "manifest.json");
                var manifest = new Manifest.Manifest
                {
                    Version = "1.0.0",
                    BaseUrl = "http://localhost/",
                    Signature = new ManifestSignature
                    {
                        Algorithm = "ed25519",
                        Signature = "c2lnbmF0dXJl",
                        PublicKey = "cHVibGljS2V5"
                    }
                };

                await ManifestSerializer.WriteAsync(manifest, path, CancellationToken.None);
                Manifest.Manifest loaded = await ManifestSerializer.ReadAsync(path, CancellationToken.None);

                Assert.NotNull(loaded.Signature);
                Assert.Equal("ed25519", loaded.Signature!.Algorithm);
                Assert.Equal("c2lnbmF0dXJl", loaded.Signature.Signature);
                Assert.Equal("cHVibGljS2V5", loaded.Signature.PublicKey);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task WriteAsync_ReadAsync_MultipleFiles()
        {
            string tempDir = CreateTempDir();
            try
            {
                string path = Path.Combine(tempDir, "manifest.json");
                var manifest = new Manifest.Manifest
                {
                    Version = "3.0.0",
                    BaseUrl = "http://cdn.example.com/",
                    Files = new List<ManifestFile>
                    {
                        new ManifestFile { Path = "a.dll", Size = 100, FileHash = "sha256:aaa", ChunkSize = 4 },
                        new ManifestFile { Path = "b.dll", Size = 200, FileHash = "sha256:bbb", ChunkSize = 4 },
                        new ManifestFile { Path = "sub/c.dll", Size = 300, FileHash = "sha256:ccc", ChunkSize = 4 }
                    }
                };

                await ManifestSerializer.WriteAsync(manifest, path, CancellationToken.None);
                Manifest.Manifest loaded = await ManifestSerializer.ReadAsync(path, CancellationToken.None);

                Assert.Equal(3, loaded.Files.Count);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        private static string CreateTempDir()
        {
            string path = Path.Combine(Path.GetTempPath(), "hina-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
