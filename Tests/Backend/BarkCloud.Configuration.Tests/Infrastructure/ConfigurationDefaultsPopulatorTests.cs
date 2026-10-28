using BarkCloud.Configuration.Domain;
using BarkCloud.Configuration.Infrastructure;
using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Shared.Identity;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;

using System.IdentityModel.Tokens.Jwt;
using System.Text;

namespace BarkCloud.Configuration.Tests.Infrastructure;

public sealed class ConfigurationDefaultsPopulatorTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ConfigurationContext _context;

    public ConfigurationDefaultsPopulatorTests()
    {
        _connection.Open();
        _context = new ConfigurationContext(new DbContextOptionsBuilder<ConfigurationContext>()
            .UseSqlite(_connection)
            .Options);
        _context.Database.EnsureCreated();
    }

    [Fact]
    public async Task Populate_FillsOnlyEmptyRowsAndKeepsGeneratedValuesStable()
    {
        var populator = CreatePopulator(minioAccessKey: "safe-access", minioSecretKey: "safe-secret");
        await populator.EnsureSeedAsync();
        var storage = new ConfigurationStorage(_context, new MetricsCollector());
        await storage.UpdateConfigurationAsync(
            "JwtSettings", "Issuer", "manual-issuer", ServiceId.Unknown, "admin", "test");

        await populator.PopulateDefaultsAsync();
        var first = await storage.GetAllAsync();
        await populator.PopulateDefaultsAsync();
        var second = await storage.GetAllAsync();

        // Общий HMAC-секрет JWT упразднён (RS256): записи SecretKey в каталоге и БД больше нет.
        first.Where(item => item.Section == "JwtSettings" && item.Key == "SecretKey").Should().BeEmpty();
        second.Single(item => item.ServiceId == ServiceId.Unknown
                              && item.Section == "JwtSettings"
                              && item.Key == "Issuer").Value.Should().Be("manual-issuer");
        second.Should().NotContain(item => item.ServiceId == ServiceId.Unknown
                                           && item.Section == "RabbitMQ"
                                           && (item.Key == "Username" || item.Key == "Password"));
        second.Where(item => item.ServiceId == ServiceId.Notification && item.Section == "Email" && item.Key != "AllowInsecure")
            .Should().OnlyContain(item => item.Value == string.Empty);
        second.Single(item => item.ServiceId == ServiceId.Notification
                              && item.Section == "Email"
                              && item.Key == "AllowInsecure").Value.Should().Be("true");

        var universal = await _context.StorageProfiles.SingleAsync();
        universal.ProfileId.Should().Be("universal-v1");
        universal.BucketName.Should().Be("cloud-universal");
        (await _context.StorageProfileRevisions.SingleAsync()).ChangeKind.Should().Be("Create");
    }

    [Fact]
    public async Task Populate_DoesNotCreatePartialUniversalProfile()
    {
        var populator = CreatePopulator(minioAccessKey: "", minioSecretKey: "");

        await populator.EnsureSeedAsync();
        await populator.PopulateDefaultsAsync();

        (await _context.StorageProfiles.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Startup_SeedsIdentityServiceHostRequiredByServiceTokenClient()
    {
        var populator = CreatePopulator(minioAccessKey: "", minioSecretKey: "");

        await populator.EnsureSeedAsync();
        await populator.PopulateDefaultsAsync();

        var storage = new ConfigurationStorage(_context, new MetricsCollector());
        var identitySettings = await storage.GetAllAsync();
        identitySettings.Single(x => x.ServiceId == ServiceId.Identity
                                     && x.Section == "IdentityService"
                                     && x.Key == "Host").Value.Should().Be("http://cloud-identity:7020");
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(18000)]
    public async Task Startup_ExistingDeployment_SeedsNewHostsFromPersistedServicePort(int port)
    {
        var populator = CreatePopulator(minioAccessKey: "", minioSecretKey: "");
        await populator.EnsureSeedAsync();
        var storage = new ConfigurationStorage(_context, new MetricsCollector());
        await _context.Settings(SettingsScopes.Get(ServiceId.Identity))
            .Where(row => row.Key == "RunSettings:Port")
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Value, port.ToString()));
        await _context.Settings(SettingsScopes.Get(ServiceId.Identity))
            .Where(row => row.Key == "IdentityService:Host").ExecuteDeleteAsync();
        _context.ChangeTracker.Clear();

        await populator.EnsureSeedAsync();
        await populator.PopulateDefaultsAsync();
        await populator.PopulateDefaultsAsync();

        var settings = await storage.GetAllAsync();
        settings.Where(row => row.Section == "IdentityService" && row.Key == "Host")
            .Should().OnlyContain(row => row.Value == $"http://cloud-identity:{port}");
        settings.Single(row => row.ServiceId == ServiceId.Identity
                               && row.Section == "RunSettings" && row.Key == "Port")
            .Value.Should().Be(port.ToString());
    }

    [Theory]
    [InlineData(ServiceId.Users)]
    [InlineData(ServiceId.Files)]
    [InlineData(ServiceId.Torrent)]
    public async Task Startup_ExistingDeployment_GetsIdentityFeedHostButNoServiceToken(ServiceId serviceId)
    {
        var populator = CreatePopulator(minioAccessKey: "", minioSecretKey: "");
        await populator.EnsureSeedAsync();
        await populator.PopulateDefaultsAsync();
        var scope = SettingsScopes.Get(serviceId);
        await _context.Settings(scope)
            .Where(x => x.Key == "IdentityService:Host" || x.Key == "IdentityService:Token").ExecuteDeleteAsync();
        _context.ChangeTracker.Clear();

        await populator.EnsureSeedAsync();
        await populator.PopulateDefaultsAsync();

        var storage = new ConfigurationStorage(_context, new MetricsCollector());
        var settings = await storage.GetAllAsync();
        new Uri(settings.Single(x => x.ServiceId == serviceId && x.Section == "IdentityService" && x.Key == "Host").Value)
            .Host.Should().Be("cloud-identity");

        // Сервисные JWT больше не генерируются и не хранятся в Configuration:
        // их выдаёт Identity (ServiceCredentialsApi) по секрету сервиса.
        settings.Where(x => x.ServiceId == serviceId && x.Section == "IdentityService" && x.Key == "Token")
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Populate_PreservesExplicitServiceAddress()
    {
        var populator = CreatePopulator(minioAccessKey: "", minioSecretKey: "");
        await populator.EnsureSeedAsync();
        var storage = new ConfigurationStorage(_context, new MetricsCollector());
        await storage.UpdateConfigurationAsync(
            "IdentityService", "Host", "https://identity.internal:18443", ServiceId.Identity, "admin", "test");

        await populator.PopulateDefaultsAsync();
        await populator.PopulateDefaultsAsync();

        var settings = await storage.GetConfiguration(ServiceId.Identity);
        settings.Single(row => row.Section == "IdentityService" && row.Key == "Host")
            .Value.Should().Be("https://identity.internal:18443");
    }

    [Fact]
    public async Task Populate_DoesNotGenerateServiceTokens()
    {
        var populator = CreatePopulator(minioAccessKey: "", minioSecretKey: "");

        await populator.EnsureSeedAsync();
        await populator.PopulateDefaultsAsync();

        var storage = new ConfigurationStorage(_context, new MetricsCollector());
        var settings = await storage.GetAllAsync();
        settings.Where(x => x.Section.EndsWith("Service", StringComparison.Ordinal) && x.Key == "Token")
            .Should().OnlyContain(x => x.Value == string.Empty);
    }

    [Fact]
    public async Task Populate_RefreshesPersistedDeploymentCredentials()
    {
        var populator = CreatePopulator(minioAccessKey: "", minioSecretKey: "");
        await populator.EnsureSeedAsync();
        await populator.PopulateDefaultsAsync();
        await _context.Settings(SettingsScopes.Get(ServiceId.Torrent))
            .Where(row => row.Key == "TorrentDb" || row.Key == "RabbitMQ:Password")
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Value, "previous-credential"));
        _context.ChangeTracker.Clear();

        await populator.PopulateDefaultsAsync();

        var settings = await new ConfigurationStorage(_context, new MetricsCollector()).GetConfiguration(ServiceId.Torrent);
        settings.Single(row => row.Section == "RabbitMQ" && row.Key == "Password")
            .Value.Should().Be("torrent-rabbit-secret");
        new Npgsql.NpgsqlConnectionStringBuilder(settings.Single(row => row.Section == "TorrentDb").Value)
            .Password.Should().Be("torrent-db-secret");
    }

    [Fact]
    public async Task Populate_UsesOnlyPerServiceDatabaseAndRabbitCredentials()
    {
        var populator = CreatePopulator(minioAccessKey: "", minioSecretKey: "");
        await populator.EnsureSeedAsync();
        await populator.PopulateDefaultsAsync();

        var settings = await new ConfigurationStorage(_context, new MetricsCollector()).GetAllAsync();
        foreach (var (serviceId, database, username, databasePassword, rabbitPassword) in new[]
                 {
                     (ServiceId.Identity, "IdentityDb", "barkcloud_identity", "identity-db-secret", "identity-rabbit-secret"),
                     (ServiceId.Users, "UsersDb", "barkcloud_users", "users-db-secret", "users-rabbit-secret"),
                     (ServiceId.Files, "FilesDb", "barkcloud_files", "files-db-secret", "files-rabbit-secret"),
                     (ServiceId.Torrent, "TorrentDb", "barkcloud_torrent", "torrent-db-secret", "torrent-rabbit-secret")
                 })
        {
            var connectionString = settings.Single(x => x.ServiceId == serviceId && x.Section == database).Value;
            var connection = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
            connection.Database.Should().Be(database.Replace("Db", "", StringComparison.Ordinal).ToLowerInvariant());
            connection.Username.Should().Be(username);
            connection.Password.Should().Be(databasePassword);

            settings.Single(x => x.ServiceId == serviceId && x.Section == "RabbitMQ" && x.Key == "Username")
                .Value.Should().Be(username);
            settings.Single(x => x.ServiceId == serviceId && x.Section == "RabbitMQ" && x.Key == "Password")
                .Value.Should().Be(rabbitPassword);
        }

        settings.Single(x => x.ServiceId == ServiceId.Notification && x.Section == "RabbitMQ" && x.Key == "Username")
            .Value.Should().Be("barkcloud_notification");
        settings.Single(x => x.ServiceId == ServiceId.Notification && x.Section == "RabbitMQ" && x.Key == "Password")
            .Value.Should().Be("notification-rabbit-secret");
        settings.Should().NotContain(x => x.ServiceId == ServiceId.Unknown
                                          && x.Section == "RabbitMQ"
                                          && (x.Key == "Username" || x.Key == "Password"));
    }

    private ConfigurationDefaultsPopulator CreatePopulator(string minioAccessKey, string minioSecretKey) => new(
        _context,
        NullLogger<ConfigurationDefaultsPopulator>.Instance,
        "postgres",
        5432,
        new Dictionary<ServiceId, string>
        {
            [ServiceId.Identity] = "identity-db-secret",
            [ServiceId.Users] = "users-db-secret",
            [ServiceId.Files] = "files-db-secret",
            [ServiceId.Torrent] = "torrent-db-secret"
        },
        "rabbitmq",
        "/",
        new Dictionary<ServiceId, string>
        {
            [ServiceId.Identity] = "identity-rabbit-secret",
            [ServiceId.Users] = "users-rabbit-secret",
            [ServiceId.Files] = "files-rabbit-secret",
            [ServiceId.Torrent] = "torrent-rabbit-secret",
            [ServiceId.Notification] = "notification-rabbit-secret"
        },
        "minio",
        "9000",
        minioAccessKey,
        minioSecretKey,
        "",
        "",
        "",
        "",
        "",
        "",
        "",
        "",
        requireExternalEndpoints: false,
        metrics: new MetricsCollector());

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }
}
