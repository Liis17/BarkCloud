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
            "StorageProfileRevisions"
        ]);
        tables.Should().NotContain("Configurations");

        context.Model.FindEntityType(typeof(StorageProfile))!
            .FindProperty(nameof(StorageProfile.QuotaBytes))!.ClrType.Should().Be(typeof(long));
    }

    [Fact]
    public void QuotaMigration_AddsNonNullableColumnWithUnlimitedDefault()
    {
        var options = new DbContextOptionsBuilder<ConfigurationContext>()
            .UseNpgsql("Host=localhost;Database=configuration;Username=postgres;Password=postgres")
            .Options;
        using var context = new ConfigurationContext(options);
        var migrations = context.GetService<IMigrationsAssembly>();
        var migrationType = migrations.Migrations["20260925080545_AddStorageProfileQuota"];
        var migration = migrations.CreateMigration(migrationType, context.Database.ProviderName!);
        var quota = migration.UpOperations.OfType<AddColumnOperation>().Single(operation => operation.Name == "QuotaBytes");

        quota.Table.Should().Be("StorageProfiles");
        quota.IsNullable.Should().BeFalse();
        quota.DefaultValue.Should().Be(0L);
    }
}
