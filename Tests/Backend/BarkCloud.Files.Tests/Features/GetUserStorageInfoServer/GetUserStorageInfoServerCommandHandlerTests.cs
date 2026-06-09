using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.GetUserStorageInfoServer;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Proto.Users;
using BarkCloud.TestKit;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Features.GetUserStorageInfoServer;

public class GetUserStorageInfoServerCommandHandlerTests
{
    private readonly Mock<IUploadedFilesStorage> _files = new();
    private readonly Mock<IPhysicalStorageStatsProvider> _storageStats = new();
    private readonly Mock<UsersServerApi.UsersServerApiClient> _usersClient = new();

    private GetUserStorageInfoServerCommandHandler CreateSut() => new(
        _files.Object,
        _storageStats.Object,
        _usersClient.Object,
        NullLogger<GetUserStorageInfoServerCommandHandler>.Instance);

    [Fact]
    public async Task Handle_QueriesByExplicitUserId()
    {
        _usersClient.Setup(c => c.GetByIdAsync(It.Is<GetByIdRequest>(r => r.UserId == 7), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetByIdResponse
            {
                User = new User { Id = 7, StorageLimitGb = 2 }
        }));
        _files.Setup(s => s.GetUserStorageUsed(7)).ReturnsAsync(100L);
        _files.Setup(s => s.GetUserStorageByType(7)).ReturnsAsync(new Dictionary<UploadFileType, long>());
        _storageStats.Setup(s => s.GetStatsAsync(default))
            .ReturnsAsync(new PhysicalStorageStats(5_000, 2_000, 1_000, 2_000));

        var response = await CreateSut().Handle(new GetUserStorageInfoServerCommand { UserId = 7 }, default);

        response.TotalUsedStorage.Should().Be(100);
        response.StorageLimit.Should().Be(2L * 1024 * 1024 * 1024);
        response.TotalAvailableStorage.Should().Be(5_000);
        response.DiskUsedStorage.Should().Be(1_000);
        response.S3UsedStorage.Should().Be(2_000);
    }

    [Fact]
    public async Task Handle_WhenUserLimitIsZero_UsesDiskTotalAsLimit()
    {
        _usersClient.Setup(c => c.GetByIdAsync(It.Is<GetByIdRequest>(r => r.UserId == 7), null, null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new GetByIdResponse
            {
                User = new User { Id = 7, StorageLimitGb = 0 }
            }));
        _files.Setup(s => s.GetUserStorageUsed(7)).ReturnsAsync(100L);
        _files.Setup(s => s.GetUserStorageByType(7)).ReturnsAsync(new Dictionary<UploadFileType, long>());
        _storageStats.Setup(s => s.GetStatsAsync(default))
            .ReturnsAsync(new PhysicalStorageStats(5_000, 2_000, 1_000, 2_000));

        var response = await CreateSut().Handle(new GetUserStorageInfoServerCommand { UserId = 7 }, default);

        response.StorageLimit.Should().Be(5_000);
    }
}
