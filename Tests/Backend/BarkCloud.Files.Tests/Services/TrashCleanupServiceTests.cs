using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Services;

public class TrashCleanupServiceTests
{
    [Fact]
    public async Task Worker_PurgesExpiredBatchViaExpiredPurge_WithCutoffNow()
    {
        var batch = new List<CloudFileEntry>
        {
            new() { Id = Guid.NewGuid(), OwnerId = 1, IsDeleted = true, PurgeAt = DateTime.UtcNow.AddDays(-1) },
        };
        var storage = new Mock<ICloudHierarchyStorage>();
        storage.Setup(s => s.GetExpiredTrashedEntries(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(batch);

        var purged = new TaskCompletionSource<DateTime>();
        var purge = new Mock<ITrashPurgeService>();
        purge.Setup(p => p.PurgeExpiredEntriesAsync(batch, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyCollection<CloudFileEntry>, DateTime, CancellationToken>((_, now, _) => purged.TrySetResult(now))
            .ReturnsAsync(new TrashPurgeResult(1, 1));

        var services = new ServiceCollection();
        services.AddSingleton(storage.Object);
        services.AddSingleton(purge.Object);
        await using var provider = services.BuildServiceProvider();

        var before = DateTime.UtcNow;
        var sut = new TrashCleanupService(
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<TrashCleanupService>.Instance);

        await sut.StartAsync(default);
        var cutoff = await purged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await sut.StopAsync(default);

        cutoff.Should().BeOnOrAfter(before);
        purge.Verify(p => p.PurgeEntriesAsync(It.IsAny<IReadOnlyCollection<CloudFileEntry>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
