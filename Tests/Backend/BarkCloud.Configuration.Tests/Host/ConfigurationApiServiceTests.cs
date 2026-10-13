using BarkCloud.Configuration.Host;
using BarkCloud.Configuration.Infrastructure;
using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Proto.Configuration;
using BarkCloud.TestKit;

using MediatR;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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
