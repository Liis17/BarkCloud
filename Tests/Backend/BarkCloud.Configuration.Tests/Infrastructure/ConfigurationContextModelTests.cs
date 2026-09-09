using BarkCloud.Configuration.Infrastructure;

using Microsoft.EntityFrameworkCore;

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
    }
}
