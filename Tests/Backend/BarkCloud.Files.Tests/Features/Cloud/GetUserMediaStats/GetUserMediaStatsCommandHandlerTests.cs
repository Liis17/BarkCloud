using BarkCloud.Files.Features.Cloud.GetUserMediaStats;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;

using DomainMediaKind = BarkCloud.Files.Domain.MediaKind;

namespace BarkCloud.Files.Tests.Features.Cloud.GetUserMediaStats;

public class GetUserMediaStatsCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<IUploadedFilesStorage> _files = new();

    [Fact]
    public async Task Handle_ReturnsStatsForAuthenticatedOwnerAndRequestedKind()
    {
        _files.Setup(storage => storage.GetUserMediaStats(OwnerId, DomainMediaKind.Video, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserMediaStats(236, 10L * 1024 * 1024 * 1024));
        var handler = new GetUserMediaStatsCommandHandler(_files.Object, UserContextFactory.Create(OwnerId));

        var response = await handler.Handle(new GetUserMediaStatsCommand { Kind = DomainMediaKind.Video }, default);

        response.TotalCount.Should().Be(236);
        response.TotalSizeBytes.Should().Be(10L * 1024 * 1024 * 1024);
    }
}
