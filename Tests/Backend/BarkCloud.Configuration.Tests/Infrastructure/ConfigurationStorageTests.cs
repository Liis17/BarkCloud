using BarkCloud.Configuration.Domain;
using BarkCloud.Configuration.Infrastructure;
using BarkCloud.Configuration.Persistence.Migrations;
using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Shared.Identity;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

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
    public async Task IsolateServiceCredentialsMigration_RemovesSecretsAndHistoryButKeepsOtherSettings()
    {
        var global = _context.Settings(SettingsScopes.Get(ServiceId.Unknown));
        global.AddRange(
            new SettingRow { Key = "RabbitMQ:Password", Value = "legacy-broker-password" },
            new SettingRow { Key = "JwtSettings:Issuer", Value = "keep-issuer" });
        _context.Settings(SettingsScopes.Get(ServiceId.Identity)).Add(
            new SettingRow { Key = "IdentityDb", Value = "legacy-admin-connection" });
        var secretRevision = new SettingRevision
        {
            SettingsTable = "GlobalSettings", Key = "RabbitMQ:Password", NewValue = "legacy-broker-password"
        };
        _context.SettingsHistory.Add(secretRevision);
        await _context.SaveChangesAsync();
        _context.SettingsHistory.Add(new SettingRevision
        {
            SettingsTable = "GlobalSettings", Key = "JwtSettings:Issuer", NewValue = "keep-issuer",
            SourceRevisionId = secretRevision.Id
        });
        await _context.SaveChangesAsync();

        var migration = new IsolateServiceCredentials();
        // Both commands use portable SQL, so execute the actual migration against the isolated test database.
        foreach (var operation in migration.UpOperations.Cast<SqlOperation>())
            await _context.Database.ExecuteSqlRawAsync(operation.Sql);

        _context.ChangeTracker.Clear();
        (await global.SingleAsync()).Value.Should().Be("keep-issuer");
        (await _context.Settings(SettingsScopes.Get(ServiceId.Identity)).CountAsync()).Should().Be(0);
        var remainingRevision = await _context.SettingsHistory.SingleAsync();
        remainingRevision.NewValue.Should().Be("keep-issuer");
        remainingRevision.SourceRevisionId.Should().BeNull();
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
    public async Task GetConfiguration_ExposesOnlyTheRequestingServicesRabbitCredentials()
    {
        await _storage.SeedMissingCatalogRowsAsync(entry =>
        {
            if (entry.Section == "RabbitMQ" && entry.Key == "Host") return "rabbitmq.internal";
            if (entry.Section == "RabbitMQ" && entry.Key == "VirtualHost") return "/";
            if (entry.Section == "RabbitMQ" && entry.Key == "Username") return $"barkcloud_{entry.ServiceId.ToString().ToLowerInvariant()}";
            if (entry.Section == "RabbitMQ" && entry.Key == "Password") return $"{entry.ServiceId}-secret";
            return "seed";
        });

        var torrentSettings = await _storage.GetConfiguration(ServiceId.Torrent);

        torrentSettings.Single(x => x.Section == "RabbitMQ" && x.Key == "Host").Value.Should().Be("rabbitmq.internal");
        torrentSettings.Single(x => x.Section == "RabbitMQ" && x.Key == "VirtualHost").Value.Should().Be("/");
        torrentSettings.Single(x => x.Section == "RabbitMQ" && x.Key == "Username").Value.Should().Be("barkcloud_torrent");
        torrentSettings.Single(x => x.Section == "RabbitMQ" && x.Key == "Password").Value.Should().Be("Torrent-secret");
        torrentSettings.Should().NotContain(x => x.Value == "barkcloud_identity" || x.Value == "Identity-secret");
        torrentSettings.Should().NotContain(x => x.ServiceId == ServiceId.Unknown
                                                 && x.Section == "RabbitMQ"
                                                 && (x.Key == "Username" || x.Key == "Password"));
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
