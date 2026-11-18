using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Shared.Queue.Users;
using BarkCloud.Torrent.Consumers;
using BarkCloud.Torrent.Domain;
using BarkCloud.Torrent.Infrastructure;
using BarkCloud.Torrent.Tests._Helpers;

using MassTransit;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Torrent.Tests.Host;

/// <summary>Features:TorrentEnabled=false: сервис не запускает движок и API, но чистит данные удалённых аккаунтов.</summary>
public class TorrentDisabledModeTests
{
    [Fact]
    public void AddTorrentServices_WhenDisabled_RegistersOnlyUserDeletedCleanup()
    {
        var services = new ServiceCollection();

        Program.AddTorrentServices(services, new ConfigurationBuilder().Build(), enabled: false);

        var hosted = services.Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ToList();
        hosted.Should().NotContain([typeof(TorrentStartupService), typeof(TorrentPersistenceService), typeof(RevocationSyncService)]);
        services.Should().Contain(d => d.ServiceType == typeof(UserDeletedConsumer));
        services.Should().NotContain(d => d.ServiceType == typeof(TorrentImportService));
    }

    [Fact]
    public async Task UserDeletedConsumer_WithUninitializedEngine_RemovesRowsAndDirectory()
    {
        using var database = new SqliteTorrentDatabase();
        const long userId = 42;
        var otherId = Guid.NewGuid();
        await database.SeedAsync(
            new TorrentEntity { Id = Guid.NewGuid(), UserId = userId, InfoHash = "a", MagnetUri = "magnet:?xt=urn:btih:a" },
            new TorrentEntity { Id = otherId, UserId = userId + 1, InfoHash = "b", MagnetUri = "magnet:?xt=urn:btih:b" });

        var downloadPath = Path.Combine(Path.GetTempPath(), $"torrent-disabled-{Guid.NewGuid():N}");
        var userDir = Path.Combine(downloadPath, userId.ToString());
        Directory.CreateDirectory(userDir);
        await File.WriteAllTextAsync(Path.Combine(userDir, "orphan.bin"), "x");

        try
        {
            await using var engine = new TorrentEngineService(NullLogger<TorrentEngineService>.Instance);
            await using var context = database.CreateAdditionalContext();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Torrent:DownloadPath"] = downloadPath })
                .Build();
            var consumer = new UserDeletedConsumer(context, engine, configuration, new MetricsCollector(),
                NullLogger<UserDeletedConsumer>.Instance);
            var consumeContext = new Mock<ConsumeContext<UserDeleted>>();
            consumeContext.SetupGet(c => c.Message).Returns(new UserDeleted { UserId = userId });

            await consumer.Consume(consumeContext.Object);

            Directory.Exists(userDir).Should().BeFalse();
            await using var check = database.CreateAdditionalContext();
            check.Torrents.Select(t => t.Id).Should().Equal(otherId);
        }
        finally
        {
            if (Directory.Exists(downloadPath))
                Directory.Delete(downloadPath, recursive: true);
        }
    }
}
