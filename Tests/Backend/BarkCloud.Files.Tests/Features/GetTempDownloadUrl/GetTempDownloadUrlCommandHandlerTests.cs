using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.GetTempDownloadUrl;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.GetTempDownloadUrl;

public class GetTempDownloadUrlCommandHandlerTests
{
    private const long OwnerId = 42;
    private const long OtherUserId = 7;

    private readonly Mock<IUploadedFilesStorage> _files = new();
    private readonly Mock<ITempFilesStorage> _temp = new();

    private GetTempDownloadUrlCommandHandler CreateSut(long userId = OwnerId) => new(
        _files.Object,
        _temp.Object,
        UserContextFactory.Create(userId),
        new RunSettings { Host = "http://localhost", Http1Port = 7026 },
        TestConfiguration.Empty(),
        NullLogger<GetTempDownloadUrlCommandHandler>.Instance);

    private static UploadFileEntity ReadyFile(Guid id, params long[] uploaders) => new()
    {
        Id = id,
        Etag = "etag",
        UploadedAt = DateTime.UtcNow,
        Uploaders = uploaders.ToList()
    };

    [Fact]
    public async Task Handle_FilesNotFound_ThrowsFileNotFound()
    {
        _files.Setup(s => s.GetFiles(It.IsAny<List<Guid>>())).ReturnsAsync((List<UploadFileEntity>)null!);

        var act = () => CreateSut().Handle(new GetTempDownloadUrlCommand { FileIds = new List<Guid> { Guid.NewGuid() } }, default);

        await act.Should().ThrowAsync<System.IO.FileNotFoundException>();
    }

    [Fact]
    public async Task Handle_OwnFiles_CreatesTempLinksAndBuildsUrls()
    {
        var fileId1 = Guid.NewGuid();
        var fileId2 = Guid.NewGuid();
        var tempId1 = Guid.NewGuid();
        var tempId2 = Guid.NewGuid();

        _files.Setup(s => s.GetFiles(It.IsAny<List<Guid>>())).ReturnsAsync(new List<UploadFileEntity>
        {
            ReadyFile(fileId1, OwnerId),
            ReadyFile(fileId2, OwnerId, OtherUserId)
        });
        _temp.Setup(s => s.CreateTempFilesBatchAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TempFile>
            {
                new() { Id = tempId1, OriginalFileId = fileId1 },
                new() { Id = tempId2, OriginalFileId = fileId2 }
            });

        var response = await CreateSut().Handle(
            new GetTempDownloadUrlCommand { FileIds = new List<Guid> { fileId1, fileId2 } }, default);

        response.FileUrls.Should().HaveCount(2);
        response.FileUrls[0].FileId.Should().Be(fileId1.ToString());
        response.FileUrls[0].Url.Should().Contain($"/download/{tempId1}");
        response.FileUrls[1].FileId.Should().Be(fileId2.ToString());
        response.FileUrls[1].Url.Should().Contain($"/download/{tempId2}");
    }

    [Fact]
    public async Task Handle_ForeignFile_ThrowsAccessDeniedAndCreatesNoTempFile()
    {
        var foreignId = Guid.NewGuid();
        _files.Setup(s => s.GetFiles(It.IsAny<List<Guid>>())).ReturnsAsync(new List<UploadFileEntity>
        {
            ReadyFile(foreignId, OtherUserId)
        });

        var act = () => CreateSut().Handle(
            new GetTempDownloadUrlCommand { FileIds = new List<Guid> { foreignId } }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
        _temp.Verify(s => s.CreateTempFilesBatchAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_OwnAndForeignFiles_ThrowsAccessDeniedAndCreatesNoTempFile()
    {
        var ownId = Guid.NewGuid();
        var foreignId = Guid.NewGuid();
        _files.Setup(s => s.GetFiles(It.IsAny<List<Guid>>())).ReturnsAsync(new List<UploadFileEntity>
        {
            ReadyFile(ownId, OwnerId),
            ReadyFile(foreignId, OtherUserId)
        });

        var act = () => CreateSut().Handle(
            new GetTempDownloadUrlCommand { FileIds = new List<Guid> { ownId, foreignId } }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
        _temp.Verify(s => s.CreateTempFilesBatchAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_FileWithoutUploaders_ThrowsAccessDenied()
    {
        var orphanId = Guid.NewGuid();
        _files.Setup(s => s.GetFiles(It.IsAny<List<Guid>>())).ReturnsAsync(new List<UploadFileEntity>
        {
            ReadyFile(orphanId)
        });

        var act = () => CreateSut().Handle(
            new GetTempDownloadUrlCommand { FileIds = new List<Guid> { orphanId } }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
    }

    [Fact]
    public async Task Handle_MissingAndNotReadyIds_AreSkippedWhileOwnFileIsIssued()
    {
        var ownId = Guid.NewGuid();
        var notReadyId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        var tempId = Guid.NewGuid();

        _files.Setup(s => s.GetFiles(It.IsAny<List<Guid>>())).ReturnsAsync(new List<UploadFileEntity>
        {
            ReadyFile(ownId, OwnerId),
            new() { Id = notReadyId, Uploaders = new List<long> { OtherUserId } } // UploadedAt/Etag не заданы → не готов
        });
        _temp.Setup(s => s.CreateTempFilesBatchAsync(
                It.Is<IEnumerable<Guid>>(ids => ids.SequenceEqual(new[] { ownId })), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TempFile> { new() { Id = tempId, OriginalFileId = ownId } });

        var response = await CreateSut().Handle(
            new GetTempDownloadUrlCommand { FileIds = new List<Guid> { ownId, notReadyId, missingId } }, default);

        response.FileUrls.Should().ContainSingle();
        response.FileUrls[0].FileId.Should().Be(ownId.ToString());
    }
}
