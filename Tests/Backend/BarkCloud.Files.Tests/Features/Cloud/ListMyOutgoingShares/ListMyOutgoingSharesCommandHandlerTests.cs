using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.ListMyOutgoingShares;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;

namespace BarkCloud.Files.Tests.Features.Cloud.ListMyOutgoingShares;

public class ListMyOutgoingSharesCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<IGrantStorage> _grants = new();

    private ListMyOutgoingSharesCommandHandler CreateSut() => new(_grants.Object, UserContextFactory.Create(OwnerId));

    [Fact]
    public async Task Handle_MapsGrantsToEntries()
    {
        var fileId = Guid.NewGuid();
        var grantId = Guid.NewGuid();
        _grants.Setup(s => s.ListByOwnerFile(OwnerId, fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FileGrant>
            {
                new() { Id = grantId, OwnerId = OwnerId, RecipientId = 7, FileId = fileId, CreatedAt = DateTime.UtcNow }
            });

        var response = await CreateSut().Handle(new ListMyOutgoingSharesCommand { FileId = fileId }, default);

        response.Items.Should().ContainSingle();
        response.Items[0].GrantId.Should().Be(grantId.ToString());
        response.Items[0].RecipientUserId.Should().Be(7);
    }
}
