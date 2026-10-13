using BarkCloud.Configuration.Infrastructure;
using BarkCloud.Configuration.Domain;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BarkCloud.Configuration.Tests.Infrastructure;

public class ConfigurationContextModelTests
{
    [Fact]
    public void Model_MapsASeparateTableForEveryBarkCloudService()
    {
        var options = new DbContextOptionsBuilder<ConfigurationContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        using var context = new ConfigurationContext(options);

        var tables = context.Model.GetEntityTypes()
            .Select(x => x.GetTableName())
            .Where(x => x is not null)
            .ToHashSet(StringComparer.Ordinal);

        tables.Should().Contain([
            "GlobalSettings",
            "IdentitySettings",
            "UsersSettings",
            "NotificationSettings",
            "FilesSettings",
            "WebSettings",
            "TorrentSettings",
            "SettingsHistory",
            "ReservedNames",
            "StorageProfiles",
            "StorageProfileRevisions",
            "StorageQuotaGroups",
            "StorageQuotaGroupBuckets"
        ]);
        tables.Should().NotContain("Configurations");

        context.Model.FindEntityType(typeof(StorageProfile))!
            .FindProperty("QuotaBytes").Should().BeNull();

        var group = context.Model.FindEntityType(typeof(StorageQuotaGroup))!;
        group.GetIndexes().Should().ContainSingle(index => index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual(new[] { nameof(StorageQuotaGroup.Name) }));

        var bucket = context.Model.FindEntityType(typeof(StorageQuotaGroupBucket))!;
        bucket.GetIndexes().Should().ContainSingle(index => index.IsUnique
            && index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { nameof(StorageQuotaGroupBucket.ServiceUrl), nameof(StorageQuotaGroupBucket.BucketName) }));
        bucket.GetForeignKeys().Should().ContainSingle(foreignKey => foreignKey.DeleteBehavior == DeleteBehavior.Cascade
            && foreignKey.Properties.Select(property => property.Name).SequenceEqual(new[] { nameof(StorageQuotaGroupBucket.GroupId) }));
    }

    [Fact]
    public void StorageQuotaGroupsMigration_CreatesGroupsAndRemovesProfileQuota()
    {
        var options = new DbContextOptionsBuilder<ConfigurationContext>()
            .UseNpgsql("Host=localhost;Database=configuration;Username=postgres;Password=postgres")
            .Options;
        using var context = new ConfigurationContext(options);
        var migrations = context.GetService<IMigrationsAssembly>();
        var migrationType = migrations.Migrations.Single(migration => migration.Key.EndsWith("_StorageQuotaGroups")).Value;
        var migration = migrations.CreateMigration(migrationType, context.Database.ProviderName!);
        migration.UpOperations.OfType<DropColumnOperation>().Single(operation => operation.Name == "QuotaBytes")
            .Table.Should().Be("StorageProfiles");

        migration.UpOperations.OfType<CreateTableOperation>().Select(operation => operation.Name)
            .Should().Contain(["StorageQuotaGroups", "StorageQuotaGroupBuckets"]);
        migration.UpOperations.OfType<CreateIndexOperation>().Should().Contain(index => index.IsUnique
            && index.Table == "StorageQuotaGroups" && index.Columns.SequenceEqual(new[] { "Name" }));
        migration.UpOperations.OfType<CreateIndexOperation>().Should().Contain(index => index.IsUnique
            && index.Table == "StorageQuotaGroupBuckets" && index.Columns.SequenceEqual(new[] { "ServiceUrl", "BucketName" }));
    }
}
