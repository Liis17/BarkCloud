using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.GetUserStorageInfo;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Features.GetUserStorageInfo;

public class GetUserStorageInfoCommandHandlerTests
{
    private readonly Mock<IUploadedFilesStorage> _files = new();
    private readonly Mock<IPhysicalStorageStatsProvider> _storageStats = new();
    private readonly Mock<IS3StorageStatsProvider> _s3StorageStats = new();
    private readonly Mock<IStorageQuotaService> _quota = new();

    public GetUserStorageInfoCommandHandlerTests() =>
        _s3StorageStats.Setup(provider => provider.GetStatsAsync(default))
            .ReturnsAsync(S3StorageStats.Unavailable);

    private GetUserStorageInfoCommandHandler CreateSut(long userId = 42) => new(
        _files.Object,
        _storageStats.Object,
        _s3StorageStats.Object,
        _quota.Object,
        UserContextFactory.Create(userId),
        NullLogger<GetUserStorageInfoCommandHandler>.Instance);

    [Fact]
    public async Task Handle_ReturnsLimitConvertedFromGbToBytes()
    {
        _quota.Setup(x => x.GetSnapshotAsync(42, false, default))
            .ReturnsAsync(new StorageQuotaSnapshot(5L * 1024 * 1024 * 1024, 0, 125));
        _files.Setup(s => s.GetUserStorageByType(42)).ReturnsAsync(new Dictionary<UploadFileType, long>());
        _storageStats.Setup(s => s.GetStatsAsync(default))
            .ReturnsAsync(new PhysicalStorageStats(2_000, 800, 700, 500));

        var response = await CreateSut().Handle(new GetUserStorageInfoCommand(), default);

        response.StorageLimit.Should().Be(5L * 1024 * 1024 * 1024);
        response.ReservedStorage.Should().Be(125);
        response.TotalAvailableStorage.Should().Be(2_000);
        response.DiskUsedStorage.Should().Be(700);
        response.S3UsedStorage.Should().Be(500);
    }

    [Fact]
    public async Task Handle_WhenUserLimitIsZero_ReportsUnlimited()
    {
        _quota.Setup(x => x.GetSnapshotAsync(42, false, default))
            .ReturnsAsync(new StorageQuotaSnapshot(null, 0, 0));
        _files.Setup(s => s.GetUserStorageByType(42)).ReturnsAsync(new Dictionary<UploadFileType, long>());
        _storageStats.Setup(s => s.GetStatsAsync(default))
            .ReturnsAsync(new PhysicalStorageStats(9_000, 4_000, 2_000, 3_000));

        var response = await CreateSut().Handle(new GetUserStorageInfoCommand(), default);

        response.StorageLimit.Should().Be(0);
        response.TotalAvailableStorage.Should().Be(9_000);
        response.DiskUsedStorage.Should().Be(2_000);
        response.S3UsedStorage.Should().Be(3_000);
    }

    [Fact]
    public async Task Handle_AggregatesUsedAndByType()
    {
        _quota.Setup(x => x.GetSnapshotAsync(42, false, default))
            .ReturnsAsync(new StorageQuotaSnapshot(10L * 1024 * 1024 * 1024, 1_500, 0));
        _files.Setup(s => s.GetUserStorageByType(42)).ReturnsAsync(new Dictionary<UploadFileType, long>
        {
            [UploadFileType.CloudFile] = 1_000,
            [UploadFileType.UserAvatar] = 500
        });
        _storageStats.Setup(s => s.GetStatsAsync(default))
            .ReturnsAsync(new PhysicalStorageStats(2_000, 800, 700, 500));

        var response = await CreateSut().Handle(new GetUserStorageInfoCommand(), default);

        response.TotalUsedStorage.Should().Be(1_500);
        response.StorageByTypes.Should().HaveCount(2);
    }

    [Fact]
    public async Task Handle_ReturnsAggregateS3StorageAndQuotaState()
    {
        _quota.Setup(x => x.GetSnapshotAsync(42, false, default))
            .ReturnsAsync(new StorageQuotaSnapshot(null, 0, 0));
        _files.Setup(s => s.GetUserStorageByType(42)).ReturnsAsync(new Dictionary<UploadFileType, long>());
        _storageStats.Setup(s => s.GetStatsAsync(default)).ReturnsAsync(new PhysicalStorageStats(2_000, 800, 700, 500));
        _s3StorageStats.Setup(s => s.GetStatsAsync(default)).ReturnsAsync(new S3StorageStats(1_234, 9_876, true, true));

        var response = await CreateSut().Handle(new GetUserStorageInfoCommand(), default);

        response.AllS3UsedStorage.Should().Be(1_234);
        response.AllS3QuotaStorage.Should().Be(9_876);
        response.AllS3HasFiniteQuota.Should().BeTrue();
        response.AllS3StatsAvailable.Should().BeTrue();
        response.TotalAvailableStorage.Should().Be(2_000);
        response.DiskUsedStorage.Should().Be(700);
        response.S3UsedStorage.Should().Be(500);
    }
}
