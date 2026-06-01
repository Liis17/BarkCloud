using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.ListSharedWithMe;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;

using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.Cloud.ListSharedWithMe;

public class ListSharedWithMeCommandHandlerTests
{
    private const long RecipientId = 42;
    private readonly Mock<IGrantStorage> _grants = new();
    private readonly Mock<IUploadedFilesStorage> _files = new();

    private ListSharedWithMeCommandHandler CreateSut() => new(
        _grants.Object, _files.Object,
        UserContextFactory.Create(RecipientId),
        new RunSettings { Host = "http://localhost", Http1Port = 7026 }, TestConfiguration.Empty());

    [Fact]
    public async Task Handle_ReturnsRecipientEntriesAndSkipsDeletedFiles()
    {
        var fileA = Guid.NewGuid();
        var fileGone = Guid.NewGuid();
        var grantA = Guid.NewGuid();
        _grants.Setup(s => s.ListSharedWithMePage(RecipientId, null, null, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FileGrant>
            {
                new() { Id = grantA, OwnerId = 7, RecipientId = RecipientId, FileId = fileA, CreatedAt = DateTime.UtcNow },
                new() { Id = Guid.NewGuid(), OwnerId = 8, RecipientId = RecipientId, FileId = fileGone, CreatedAt = DateTime.UtcNow }
            });
        _files.Setup(s => s.GetPreviewsForFiles(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<FilePreview>>());
        _files.Setup(s => s.GetFile(fileA)).ReturnsAsync(new UploadFileEntity { Id = fileA, Filename = "a.jpg" });
        _files.Setup(s => s.GetFile(fileGone)).ReturnsAsync((UploadFileEntity?)null);

        var response = await CreateSut().Handle(new ListSharedWithMeCommand { Limit = 50 }, default);

        // Удалённый файл пропущен; вернулась только живая запись «от кого».
        response.Items.Should().ContainSingle();
        response.Items[0].GrantId.Should().Be(grantA.ToString());
        response.Items[0].OwnerUserId.Should().Be(7);
        response.Items[0].File.FileName.Should().Be("a.jpg");
    }
}
