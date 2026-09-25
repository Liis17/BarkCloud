using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.GetUserStorageInfoServer;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Features.GetUserStorageInfoServer;

public class GetUserStorageInfoServerCommandHandlerTests
{
    private readonly Mock<IUploadedFilesStorage> _files = new();
    private readonly Mock<IPhysicalStorageStatsProvider> _storageStats = new();
    private readonly Mock<IS3StorageStatsProvider> _s3StorageStats = new();
    private readonly Mock<IStorageQuotaService> _quota = new();

    public GetUserStorageInfoServerCommandHandlerTests() =>
        _s3StorageStats.Setup(provider => provider.GetStatsAsync(default))
            .ReturnsAsync(S3StorageStats.Unavailable);

    private GetUserStorageInfoServerCommandHandler CreateSut() => new(
        _files.Object,
        _storageStats.Object,
        _s3StorageStats.Object,
        _quota.Object,
        NullLogger<GetUserStorageInfoServerCommandHandler>.Instance);

    [Fact]
    public async Task Handle_QueriesByExplicitUserId()
    {
        _quota.Setup(x => x.GetSnapshotAsync(7, false, default))
            .ReturnsAsync(new StorageQuotaSnapshot(2L * 1024 * 1024 * 1024, 100, 50));
        _files.Setup(s => s.GetUserStorageByType(7)).ReturnsAsync(new Dictionary<UploadFileType, long>());
        _storageStats.Setup(s => s.GetStatsAsync(default))
            .ReturnsAsync(new PhysicalStorageStats(5_000, 2_000, 1_000, 2_000));

        var response = await CreateSut().Handle(new GetUserStorageInfoServerCommand { UserId = 7 }, default);

        response.TotalUsedStorage.Should().Be(100);
        response.StorageLimit.Should().Be(2L * 1024 * 1024 * 1024);
        response.ReservedStorage.Should().Be(50);
        response.TotalAvailableStorage.Should().Be(5_000);
        response.DiskUsedStorage.Should().Be(1_000);
        response.S3UsedStorage.Should().Be(2_000);
    }

    [Fact]
    public async Task Handle_WhenUserLimitIsZero_ReportsUnlimited()
    {
        _quota.Setup(x => x.GetSnapshotAsync(7, false, default))
            .ReturnsAsync(new StorageQuotaSnapshot(null, 100, 0));
        _files.Setup(s => s.GetUserStorageByType(7)).ReturnsAsync(new Dictionary<UploadFileType, long>());
        _storageStats.Setup(s => s.GetStatsAsync(default))
            .ReturnsAsync(new PhysicalStorageStats(5_000, 2_000, 1_000, 2_000));

        var response = await CreateSut().Handle(new GetUserStorageInfoServerCommand { UserId = 7 }, default);

        response.StorageLimit.Should().Be(0);
    }

    [Fact]
    public async Task Handle_ReturnsAggregateS3StorageForServerApi()
    {
        _quota.Setup(x => x.GetSnapshotAsync(7, false, default))
            .ReturnsAsync(new StorageQuotaSnapshot(null, 0, 0));
        _files.Setup(s => s.GetUserStorageByType(7)).ReturnsAsync(new Dictionary<UploadFileType, long>());
        _storageStats.Setup(s => s.GetStatsAsync(default)).ReturnsAsync(new PhysicalStorageStats(5_000, 2_000, 1_000, 2_000));
        _s3StorageStats.Setup(s => s.GetStatsAsync(default)).ReturnsAsync(new S3StorageStats(321, 654, false, true));

        var response = await CreateSut().Handle(new GetUserStorageInfoServerCommand { UserId = 7 }, default);

        response.AllS3UsedStorage.Should().Be(321);
        response.AllS3QuotaStorage.Should().Be(654);
        response.AllS3HasFiniteQuota.Should().BeFalse();
        response.AllS3StatsAvailable.Should().BeTrue();
    }
}
