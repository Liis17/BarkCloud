using BarkCloud.Configuration.Infrastructure;
using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Shared.Identity;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

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
        second.Where(item => item.ServiceId == ServiceId.Notification && item.Section == "Email")
            .Should().OnlyContain(item => item.Value == string.Empty);

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
