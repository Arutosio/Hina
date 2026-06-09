using System;
using System.IO;
using System.Threading.Tasks;
using Hina.PackageManager.Registry;

namespace Hina.PackageManager.Tests
{
    public class RegistryStoreTests : IDisposable
    {
        private readonly string _tempDir;

        public RegistryStoreTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "hina-reg-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }

        [Fact]
        public void Load_MissingFile_ReturnsEmptyRegistry()
        {
            RegistryStore store = new RegistryStore(Path.Combine(_tempDir, "missing.json"));
            Registry.Registry r = store.Load();
            Assert.Empty(r.Apps);
            Assert.Equal(1, r.SchemaVersion);
        }

        [Fact]
        public async Task SaveThenLoad_RoundTripsInstalledApp()
        {
            string path = Path.Combine(_tempDir, "registry.json");
            RegistryStore store = new RegistryStore(path);

            Registry.Registry input = new Registry.Registry();
            input.Apps["fooedit"] = new InstalledApp
            {
                Name = "fooedit",
                InstalledVersion = "1.0.0",
                InstallPath = "/apps/fooedit",
                DescriptorUrl = "https://example.com/hina.app.json",
                BaseUrl = "https://cdn.example.com/fooedit/",
                Channel = "stable",
                PublicKey = "AAAA",
                InstalledAt = new DateTimeOffset(2026, 5, 17, 10, 0, 0, TimeSpan.Zero),
                LastUpdatedAt = new DateTimeOffset(2026, 5, 17, 10, 0, 0, TimeSpan.Zero),
                ExecutedHooks =
                {
                    new HookEvidence { Action = "addToPath", Identity = "addToPath:fooedit", Evidence = "/Users/x/.local/bin/fooedit" }
                },
                ShellEntries =
                {
                    new ShellEntryRecord { Id = "main", Evidence = "/Users/x/.local/share/applications/fooedit.desktop" }
                }
            };

            await store.SaveAsync(input);
            Registry.Registry loaded = store.Load();

            Assert.True(loaded.Apps.ContainsKey("fooedit"));
            InstalledApp app = loaded.Apps["fooedit"];
            Assert.Equal("1.0.0", app.InstalledVersion);
            Assert.Single(app.ExecutedHooks);
            Assert.Equal("addToPath", app.ExecutedHooks[0].Action);
            Assert.Single(app.ShellEntries);
        }

        [Fact]
        public async Task SaveAsync_DoesNotLeaveTempFileBehind()
        {
            string path = Path.Combine(_tempDir, "registry.json");
            RegistryStore store = new RegistryStore(path);

            await store.SaveAsync(new Registry.Registry());

            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
        }

        [Theory]
        [InlineData("{\"schemaVersion\":1,\"apps\":{\"demo\":")] // truncated mid-object
        [InlineData("not json at all")]
        [InlineData("{ this is broken }")]
        public async Task Load_CorruptJson_ThrowsActionableError(string corrupt)
        {
            // A non-empty but unparseable registry must surface a clear, actionable error
            // (not a raw JSON parser message and not a silent "nothing installed"), so the
            // user knows the file is corrupt and how to recover.
            string path = Path.Combine(_tempDir, "registry.json");
            await File.WriteAllTextAsync(path, corrupt);

            RegistryStore store = new RegistryStore(path);
            RegistryCorruptException ex = Assert.Throws<RegistryCorruptException>(() => store.Load());
            Assert.Contains("corrupt", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(path, ex.Message);
        }

        [Fact]
        public async Task Load_FutureSchemaVersion_Throws()
        {
            string path = Path.Combine(_tempDir, "registry.json");
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":999,\"apps\":{}}");

            RegistryStore store = new RegistryStore(path);
            RegistrySchemaException ex = Assert.Throws<RegistrySchemaException>(() => store.Load());
            Assert.Contains("999", ex.Message);
        }

        [Fact]
        public async Task Load_CurrentSchemaVersion_Succeeds()
        {
            string path = Path.Combine(_tempDir, "registry.json");
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"apps\":{}}");

            RegistryStore store = new RegistryStore(path);
            Registry.Registry r = store.Load();
            Assert.Empty(r.Apps);
        }

        [Fact]
        public async Task SaveAsync_OverwritesPreviousFileAtomically()
        {
            string path = Path.Combine(_tempDir, "registry.json");
            RegistryStore store = new RegistryStore(path);

            Registry.Registry v1 = new Registry.Registry();
            v1.Apps["a"] = new InstalledApp { Name = "a", InstalledVersion = "1.0.0" };
            await store.SaveAsync(v1);

            Registry.Registry v2 = new Registry.Registry();
            v2.Apps["b"] = new InstalledApp { Name = "b", InstalledVersion = "2.0.0" };
            await store.SaveAsync(v2);

            Registry.Registry loaded = store.Load();
            Assert.False(loaded.Apps.ContainsKey("a"));
            Assert.True(loaded.Apps.ContainsKey("b"));
        }
    }
}
