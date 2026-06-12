using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.GetUserStorageInfo;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Proto.Users;
using BarkCloud.TestKit;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Features.GetUserStorageInfo;

public class GetUserStorageInfoCommandHandlerTests
{
    private readonly Mock<IUploadedFilesStorage> _files = new();
    private readonly Mock<IPhysicalStorageStatsProvider> _storageStats = new();
    private readonly Mock<UsersServerApi.UsersServerApiClient> _usersClient = new();

    private GetUserStorageInfoCommandHandler CreateSut(long userId = 42) => new(
        _files.Object,
        _storageStats.Object,
        UserContextFactory.Create(userId),
        _usersClient.Object,
        NullLogger<GetUserStorageInfoCommandHandler>.Instance);

    [Fact]
    public async Task Handle_ReturnsLimitConvertedFromGbToBytes()
    {
        _usersClient.Setup(c => c.GetByIdAsync(It.IsAny<GetByIdRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetByIdResponse
            {
                User = new User { Id = 42, StorageLimitGb = 5 }
            }));
        _files.Setup(s => s.GetUserStorageUsed(42)).ReturnsAsync(0L);
        _files.Setup(s => s.GetUserStorageByType(42)).ReturnsAsync(new Dictionary<UploadFileType, long>());
        _storageStats.Setup(s => s.GetStatsAsync(default))
            .ReturnsAsync(new PhysicalStorageStats(2_000, 800, 700, 500));

        var response = await CreateSut().Handle(new GetUserStorageInfoCommand(), default);

        response.StorageLimit.Should().Be(5L * 1024 * 1024 * 1024);
        response.TotalAvailableStorage.Should().Be(2_000);
        response.DiskUsedStorage.Should().Be(700);
        response.S3UsedStorage.Should().Be(500);
    }

    [Fact]
    public async Task Handle_WhenUserLimitIsZero_UsesDiskTotalAsLimit()
    {
        _usersClient.Setup(c => c.GetByIdAsync(It.IsAny<GetByIdRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetByIdResponse
            {
                User = new User { Id = 42, StorageLimitGb = 0 }
            }));
        _files.Setup(s => s.GetUserStorageUsed(42)).ReturnsAsync(0L);
        _files.Setup(s => s.GetUserStorageByType(42)).ReturnsAsync(new Dictionary<UploadFileType, long>());
        _storageStats.Setup(s => s.GetStatsAsync(default))
            .ReturnsAsync(new PhysicalStorageStats(9_000, 4_000, 2_000, 3_000));

        var response = await CreateSut().Handle(new GetUserStorageInfoCommand(), default);

        response.StorageLimit.Should().Be(9_000);
        response.TotalAvailableStorage.Should().Be(9_000);
        response.DiskUsedStorage.Should().Be(2_000);
        response.S3UsedStorage.Should().Be(3_000);
    }

    [Fact]
    public async Task Handle_AggregatesUsedAndByType()
    {
        _usersClient.Setup(c => c.GetByIdAsync(It.IsAny<GetByIdRequest>(), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetByIdResponse
            {
                User = new User { Id = 42, StorageLimitGb = 10 }
            }));
        _files.Setup(s => s.GetUserStorageUsed(42)).ReturnsAsync(1_500L);
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
}
