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
                              && item.Key == "AllowInsecure").Value.Should().Be("false");

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
    public async Task Startup_ExistingDeployment_GetsIdentityFeedHostAndServiceToken(ServiceId serviceId)
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
        var host = settings.Single(x => x.ServiceId == serviceId && x.Section == "IdentityService" && x.Key == "Host").Value;
        new Uri(host).Host.Should().Be("cloud-identity");
        var token = settings.Single(x => x.ServiceId == serviceId && x.Section == "IdentityService" && x.Key == "Token").Value;
        new JwtSecurityTokenHandler().ReadJwtToken(token).Claims
            .Should().Contain(x => x.Type == IdentityClaims.TokenType && x.Value == nameof(TokenType.Service));
        var original = token;

        await populator.EnsureSeedAsync();
        await populator.PopulateDefaultsAsync();

        (await storage.GetAllAsync()).Single(x => x.ServiceId == serviceId && x.Section == "IdentityService" && x.Key == "Token")
            .Value.Should().Be(original);
    }

    // F23: сервисные токены подписываются тем же UTF-8 ключом, которым их проверяют остальные сервисы.
    [Fact]
    public async Task Populate_UnicodeJwtSecret_SignsServiceTokensWithUtf8Key()
    {
        const string secret = "СекретныйКлючДляПодписиТокеновЮникод";
        var populator = CreatePopulator(minioAccessKey: "", minioSecretKey: "");
        await populator.EnsureSeedAsync();
        _context.ChangeTracker.Clear();
        await _context.Settings(SettingsScopes.Get(ServiceId.Unknown))
            .Where(x => x.Key == "JwtSettings:SecretKey").ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, secret));

        await populator.PopulateDefaultsAsync();

        var storage = new ConfigurationStorage(_context, new MetricsCollector());
        var settings = await storage.GetAllAsync();
        var token = settings.First(x => x.Section == "IdentityService" && x.Key == "Token").Value;
        var issuer = settings.Single(x => x.ServiceId == ServiceId.Unknown && x.Section == "JwtSettings" && x.Key == "Issuer").Value;
        var audience = settings.Single(x => x.ServiceId == ServiceId.Unknown && x.Section == "JwtSettings" && x.Key == "Audience").Value;
        var act = () => new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
            ValidIssuer = issuer,
            ValidAudience = audience,
            ValidateLifetime = true
        }, out _);
        act.Should().NotThrow();
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
