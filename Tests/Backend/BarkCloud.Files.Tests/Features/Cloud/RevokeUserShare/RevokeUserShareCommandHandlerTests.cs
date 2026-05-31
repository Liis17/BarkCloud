using BarkCloud.Files.Features.Cloud.RevokeUserShare;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Features.Cloud.RevokeUserShare;

public class RevokeUserShareCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<IGrantStorage> _grants = new();

    private RevokeUserShareCommandHandler CreateSut() => new(
        _grants.Object, UserContextFactory.Create(OwnerId),
        NullLogger<RevokeUserShareCommandHandler>.Instance);

    [Fact]
    public async Task Handle_RemovesOwnGrant()
    {
        var grantId = Guid.NewGuid();
        _grants.Setup(s => s.Remove(OwnerId, grantId, It.IsAny<CancellationToken>())).ReturnsAsync(1);

        await CreateSut().Handle(new RevokeUserShareCommand { GrantId = grantId }, default);

        // Удаление строго в рамках владельца (owner-scoped), идемпотентно.
        _grants.Verify(s => s.Remove(OwnerId, grantId, It.IsAny<CancellationToken>()), Times.Once);
    }
}
