using System.Collections.Generic;
using System.Text.Json.Nodes;
using Hina.PackageManager.Registry;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hina.PackageManager.Tests
{
    // Exercises the RegistryStore load path's integration with RegistryMigrator. The
    // production target is still schema 1 (no migrations), so this drives the internal
    // seam with a synthetic target+migration to prove the wiring upgrades forward.
    public class RegistryStoreMigrationTests
    {
        [Fact]
        public void LoadFromJson_OlderSchema_IsMigratedForwardThenDeserialized()
        {
            string json = "{\"schemaVersion\":1,\"apps\":{}}";
            List<RegistryMigration> migrations = new List<RegistryMigration>
            {
                new RegistryMigration(1, root => root["apps"]!.AsObject()["demo"] = new JsonObject()),
            };

            var r = RegistryStore.LoadFromJson(json, targetVersion: 2, migrations, NullLogger.Instance, "/x");

            Assert.Equal(2, r.SchemaVersion);
            Assert.True(r.Apps.ContainsKey("demo"));
        }

        [Fact]
        public void LoadFromJson_NewerSchema_StillRefused()
        {
            string json = "{\"schemaVersion\":2,\"apps\":{}}";
            Assert.Throws<RegistrySchemaException>(
                () => RegistryStore.LoadFromJson(json, targetVersion: 1, new List<RegistryMigration>(), NullLogger.Instance, "/x"));
        }

        [Fact]
        public void LoadFromJson_CurrentSchema_LoadsUnchanged()
        {
            string json = "{\"schemaVersion\":1,\"apps\":{}}";
            var r = RegistryStore.LoadFromJson(json, targetVersion: 1, new List<RegistryMigration>(), NullLogger.Instance, "/x");
            Assert.Equal(1, r.SchemaVersion);
            Assert.Empty(r.Apps);
        }
    }
}
