using BarkCloud.Configuration.Host;
using BarkCloud.Configuration.Infrastructure;
using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Proto.Configuration;
using BarkCloud.TestKit;

using MediatR;

using Grpc.Core;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Configuration.Tests.Host;

public sealed class ConfigurationApiServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ConfigurationContext _context;
    private readonly ConfigurationApiService _service;

    public ConfigurationApiServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ConfigurationContext>()
            .UseSqlite(_connection)
            .Options;
        _context = new ConfigurationContext(options);
        _context.Database.EnsureCreated();

        var metrics = new MetricsCollector();
        _service = new ConfigurationApiService(
            Mock.Of<IMediator>(),
            metrics,
            Mock.Of<IConfigurationStorage>(),
            new StorageProfileStorage(_context, metrics),
            new StorageQuotaGroupStorage(_context));
    }

    [Theory]
    [InlineData("users-bootstrap-key", true)]
    [InlineData("wrong-key", false)]
    [InlineData(null, false)]
    public async Task Readiness_UsesBootstrapAccessGate(string? providedKey, bool allowed)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["CONFIGURATION_ACCESS_KEYS"] = "Users=users-bootstrap-key;Web=web-bootstrap-key" }).Build();
        var interceptor = new ConfigurationAccessInterceptor(configuration,
            NullLogger<ConfigurationAccessInterceptor>.Instance);
        var context = new TestServerCallContext("/barkcloud.configuration.ConfigurationApi/GetReadiness");
        if (providedKey != null)
            context.RequestHeaders.Add("x-config-access-key", providedKey);

        var call = () => interceptor.UnaryServerHandler(new GetReadinessRequest(), context,
            _service.GetReadiness);

        if (allowed)
            (await call()).Ready.Should().BeTrue();
        else
            (await Assert.ThrowsAsync<RpcException>(call)).StatusCode.Should().Be(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task SaveStorageQuotaGroup_ReturnsFailureWhenQuotaCannotBeParsed()
    {
        var response = await _service.SaveStorageQuotaGroup(new SaveStorageQuotaGroupRequest
        {
            Name = "Archive",
            QuotaValue = "-1",
            QuotaUnit = "gb"
        }, new TestServerCallContext());

        response.Success.Should().BeFalse();
        response.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task DeleteStorageQuotaGroup_ReturnsFailureWhenGroupDoesNotExist()
    {
        var response = await _service.DeleteStorageQuotaGroup(new DeleteStorageQuotaGroupRequest
        {
            Id = "missing"
        }, new TestServerCallContext());

        response.Success.Should().BeFalse();
        response.Message.Should().Be("Группа S3 не найдена.");
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }
}
