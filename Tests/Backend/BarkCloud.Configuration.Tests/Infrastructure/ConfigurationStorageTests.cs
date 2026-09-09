using BarkCloud.Configuration.Domain;
using BarkCloud.Configuration.Infrastructure;
using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Shared.Identity;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Configuration.Tests.Infrastructure;

public class ConfigurationStorageTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ConfigurationContext _context;
    private readonly ConfigurationStorage _storage;

    public ConfigurationStorageTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ConfigurationContext>()
            .UseSqlite(_connection)
            .Options;
        _context = new ConfigurationContext(options);
        _context.Database.EnsureCreated();
        _storage = new ConfigurationStorage(_context, new MetricsCollector());
    }

    [Fact]
    public async Task UpdateAndRollback_AreVisibleThroughThePublicStorageInterface()
    {
        await _storage.SeedMissingCatalogRowsAsync(_ => "seed");
        await _storage.UpdateConfigurationAsync(
            "TempFiles", "ExpiresAt", "120", ServiceId.Files, "admin", "web");

        var changed = (await _storage.GetConfiguration(ServiceId.Files))
            .Single(x => x.Section == "TempFiles" && x.Key == "ExpiresAt");
        changed.Value.Should().Be("120");
        changed.EditedFrom.Should().Be("web");

        var revision = (await _storage.GetHistoryAsync(
            "TempFiles", "ExpiresAt", ServiceId.Files, 10)).Single();
        revision.PreviousValue.Should().Be("seed");
        revision.NewValue.Should().Be("120");

        await _storage.RollbackAsync(revision.Id, "admin", "web");

        var rolledBack = (await _storage.GetConfiguration(ServiceId.Files))
            .Single(x => x.Section == "TempFiles" && x.Key == "ExpiresAt");
        rolledBack.Value.Should().Be("seed");
    }

    [Fact]
    public async Task Update_UnknownSetting_IsRejected()
    {
        var act = () => _storage.UpdateConfigurationAsync(
            "Unknown", "Value", "x", ServiceId.Files, "admin", "web");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GetConfiguration_ServiceValueOverridesGlobalValueWithTheSameStorageKey()
    {
        await _storage.SeedMissingCatalogRowsAsync(_ => "seed");
        await _storage.UpdateConfigurationAsync(
            "JwtSettings", "Issuer", "global", ServiceId.Unknown, "admin", "web");
        _context.Settings(SettingsScopes.Get(ServiceId.Files)).Add(new SettingRow
        {
            Key = "JwtSettings:Issuer",
            Value = "files-override",
            EditedAt = DateTime.UtcNow,
            EditedBy = "migration"
        });
        await _context.SaveChangesAsync();

        var settings = await _storage.GetConfiguration(ServiceId.Files);

        settings.Single(x => x.Section == "JwtSettings" && x.Key == "Issuer")
            .Value.Should().Be("files-override");
    }

    [Fact]
    public async Task GetConfiguration_ProjectsNormalizedReservedNamesForExistingUsersClient()
    {
        await _storage.AddReservedNameAsync(" Admin ");
        await _storage.AddReservedNameAsync("Support");

        var settings = await _storage.GetConfiguration(ServiceId.Users);

        settings.Single(item => item.Section == "ReservedNames" && item.Key == "Usernames")
            .Value.Should().Be("admin,support");
    }

    [Fact]
    public async Task GetAll_ExposesEmailEnabledAsComputedWithoutPersistingIt()
    {
        await _storage.SeedMissingCatalogRowsAsync(_ => string.Empty);

        var all = await _storage.GetAllAsync();

        all.Should().ContainSingle(item => item.Section == "Features"
                                           && item.Key == "EmailEnabled"
                                           && item.EditedFrom == "computed");
        (await _context.Settings(SettingsScopes.Get(ServiceId.Unknown))
                .AnyAsync(row => row.Key == "Features:EmailEnabled"))
            .Should().BeFalse();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }
}
