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
        var secret = first.Single(item => item.ServiceId == ServiceId.Unknown
                                          && item.Section == "JwtSettings"
                                          && item.Key == "SecretKey").Value;
        await populator.PopulateDefaultsAsync();
        var second = await storage.GetAllAsync();

        secret.Should().NotBeNullOrWhiteSpace();
        second.Single(item => item.ServiceId == ServiceId.Unknown
                              && item.Section == "JwtSettings"
                              && item.Key == "SecretKey").Value.Should().Be(secret);
        second.Single(item => item.ServiceId == ServiceId.Unknown
                              && item.Section == "JwtSettings"
                              && item.Key == "Issuer").Value.Should().Be("manual-issuer");
        second.Single(item => item.ServiceId == ServiceId.Unknown
                              && item.Section == "RabbitMQ"
                              && item.Key == "Username").Value.Should().BeEmpty();
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
        settings.Single(x => x.ServiceId == serviceId && x.Section == "IdentityService" && x.Key == "Token")
            .Value.Should().BeEmpty();
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

    private ConfigurationDefaultsPopulator CreatePopulator(string minioAccessKey, string minioSecretKey) => new(
        _context,
        NullLogger<ConfigurationDefaultsPopulator>.Instance,
        "postgres",
        "postgres-user",
        "postgres-secret",
        "",
        "",
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
