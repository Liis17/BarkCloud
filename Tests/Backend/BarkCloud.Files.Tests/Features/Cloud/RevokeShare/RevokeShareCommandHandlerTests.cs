using BarkCloud.Files.Features.Cloud.RevokeShare;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Features.Cloud.RevokeShare;

public class RevokeShareCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<IShareStorage> _storage = new();

    private RevokeShareCommandHandler CreateSut() => new(
        _storage.Object,
        UserContextFactory.Create(OwnerId),
        NullLogger<RevokeShareCommandHandler>.Instance);

    [Fact]
    public async Task Handle_RemovesOwnerShare()
    {
        var shareId = Guid.NewGuid();
        _storage.Setup(s => s.Remove(OwnerId, shareId, It.IsAny<CancellationToken>())).ReturnsAsync(1);

        await CreateSut().Handle(new RevokeShareCommand { ShareId = shareId }, default);

        _storage.Verify(s => s.Remove(OwnerId, shareId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_NotFound_NoError()
    {
        var shareId = Guid.NewGuid();
        _storage.Setup(s => s.Remove(OwnerId, shareId, It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var act = () => CreateSut().Handle(new RevokeShareCommand { ShareId = shareId }, default);

        await act.Should().NotThrowAsync();
    }
}
