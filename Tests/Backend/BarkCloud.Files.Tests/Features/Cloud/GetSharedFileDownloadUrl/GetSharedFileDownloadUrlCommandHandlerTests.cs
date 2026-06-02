using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.GetSharedFileDownloadUrl;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.Shared.Exceptions.Files;

using TempFileEntity = BarkCloud.Files.Domain.TempFile;
using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.Cloud.GetSharedFileDownloadUrl;

public class GetSharedFileDownloadUrlCommandHandlerTests
{
    private const long RecipientId = 42;
    private readonly Mock<IGrantStorage> _grants = new();
    private readonly Mock<IUploadedFilesStorage> _files = new();
    private readonly Mock<ITempFilesStorage> _temp = new();
    private readonly Mock<IDirectoryGrantStorage> _dirGrants = new();
    private readonly Mock<ICloudHierarchyStorage> _hierarchy = new();

    public GetSharedFileDownloadUrlCommandHandlerTests()
    {
        // По умолчанию у получателя нет грантов на папки (доступ через папку = false).
        _dirGrants.Setup(s => s.ListByRecipient(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DirectoryGrant>());
    }

    private GetSharedFileDownloadUrlCommandHandler CreateSut() => new(
        _grants.Object,
        new FolderGrantAccessService(_dirGrants.Object, _hierarchy.Object),
        _files.Object, _temp.Object,
        UserContextFactory.Create(RecipientId),
        new RunSettings { Host = "http://localhost", Http1Port = 7026 }, TestConfiguration.Empty());

    [Fact]
    public async Task Handle_NoGrant_ThrowsAccessDenied()
    {
        var fileId = Guid.NewGuid();
        _grants.Setup(s => s.RecipientHasAccess(RecipientId, fileId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var act = () => CreateSut().Handle(new GetSharedFileDownloadUrlCommand { FileId = fileId }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
        _temp.Verify(s => s.CreateTempFilesBatchAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WithGrant_ReturnsTempUrl()
    {
        var fileId = Guid.NewGuid();
        var tempId = Guid.NewGuid();
        _grants.Setup(s => s.RecipientHasAccess(RecipientId, fileId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _files.Setup(s => s.GetFile(fileId)).ReturnsAsync(new UploadFileEntity { Id = fileId });
        _temp.Setup(s => s.CreateTempFilesBatchAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TempFileEntity> { new() { Id = tempId, OriginalFileId = fileId } });

        var response = await CreateSut().Handle(new GetSharedFileDownloadUrlCommand { FileId = fileId }, default);

        response.DownloadUrl.Should().Contain(tempId.ToString());
    }
}
