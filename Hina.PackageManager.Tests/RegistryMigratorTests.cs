using System.Collections.Generic;
using System.Text.Json.Nodes;
using Hina.PackageManager.Registry;
using Xunit;

namespace Hina.PackageManager.Tests
{
    public class RegistryMigratorTests
    {
        private static JsonObject Doc(int version) => new JsonObject
        {
            ["schemaVersion"] = version,
            ["apps"] = new JsonObject(),
        };

        [Fact]
        public void Migrate_AlreadyAtTarget_LeavesDocUnchanged()
        {
            JsonObject root = Doc(1);
            JsonObject result = RegistryMigrator.Migrate(root, targetVersion: 1, new List<RegistryMigration>());
            Assert.Equal(1, (int)result["schemaVersion"]!);
        }

        [Fact]
        public void Migrate_AppliesOrderedMigrations_AndStampsTargetVersion()
        {
            JsonObject root = Doc(1);
            List<RegistryMigration> migrations = new List<RegistryMigration>
            {
                new RegistryMigration(1, o => o["foo"] = "added-by-1to2"),
                new RegistryMigration(2, o => o["bar"] = "added-by-2to3"),
            };

            JsonObject result = RegistryMigrator.Migrate(root, targetVersion: 3, migrations);

            Assert.Equal(3, (int)result["schemaVersion"]!);
            Assert.Equal("added-by-1to2", (string)result["foo"]!);
            Assert.Equal("added-by-2to3", (string)result["bar"]!);
        }

        [Fact]
        public void Migrate_MissingStepInChain_Throws()
        {
            JsonObject root = Doc(1);
            // Only a 2->3 migration; nothing to take v1 to v2.
            List<RegistryMigration> migrations = new List<RegistryMigration>
            {
                new RegistryMigration(2, o => o["bar"] = "x"),
            };

            Assert.Throws<RegistrySchemaException>(
                () => RegistryMigrator.Migrate(root, targetVersion: 3, migrations));
        }

        [Fact]
        public void Migrate_MissingSchemaVersion_TreatedAsOne()
        {
            JsonObject root = new JsonObject { ["apps"] = new JsonObject() };
            List<RegistryMigration> migrations = new List<RegistryMigration>
            {
                new RegistryMigration(1, o => o["foo"] = "y"),
            };
            JsonObject result = RegistryMigrator.Migrate(root, targetVersion: 2, migrations);
            Assert.Equal(2, (int)result["schemaVersion"]!);
            Assert.Equal("y", (string)result["foo"]!);
        }

        [Fact]
        public void Default_HasNoMigrations_WhileSchemaIsStillOne()
        {
            // Schema is still version 1, so there is nothing to migrate yet — the
            // framework is the scaffold for the first non-additive bump.
            Assert.Empty(RegistryMigrator.Default);
        }
    }
}
