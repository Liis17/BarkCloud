using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.SetVideoThumbnail;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.Extensions.Logging.Abstractions;

using FileNotFoundException = BarkCloud.Shared.Exceptions.Files.FileNotFoundException;
using UploadFileEntity = BarkCloud.Files.Domain.UploadFile;

namespace BarkCloud.Files.Tests.Features.Cloud.SetVideoThumbnail;

public class SetVideoThumbnailCommandHandlerTests
{
    private const long OwnerId = 42;
    private readonly Mock<IUploadedFilesStorage> _files = new();

    // IO-сервисы (ImageCompressor/PreviewPersistenceService/S3Uploader/S3BucketRegistry) не достигаются:
    // все проверяемые ветки бросают исключение до первого обращения к S3/сжатию.
    private SetVideoThumbnailCommandHandler CreateSut() => new(
        _files.Object, null!, null!, null!, null!,
        UserContextFactory.Create(OwnerId),
        NullLogger<SetVideoThumbnailCommandHandler>.Instance);

    [Fact]
    public async Task Handle_VideoNotFound_Throws()
    {
        var sourceId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(It.IsAny<Guid>())).ReturnsAsync((UploadFileEntity?)null);
        _files.Setup(s => s.GetFile(sourceId)).ReturnsAsync(new UploadFileEntity { Id = sourceId, MediaKind = MediaKind.Photo, Uploaders = new() { OwnerId } });

        var act = () => CreateSut().Handle(new SetVideoThumbnailCommand { VideoFileId = Guid.NewGuid(), SourceImageFileId = sourceId }, default);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task Handle_SourceNotFound_Throws()
    {
        var videoId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(It.IsAny<Guid>())).ReturnsAsync((UploadFileEntity?)null);
        _files.Setup(s => s.GetFile(videoId)).ReturnsAsync(new UploadFileEntity { Id = videoId, MediaKind = MediaKind.Video, Uploaders = new() { OwnerId } });

        var act = () => CreateSut().Handle(new SetVideoThumbnailCommand { VideoFileId = videoId, SourceImageFileId = Guid.NewGuid() }, default);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task Handle_ForeignFile_ThrowsAccessDenied()
    {
        var videoId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        _files.Setup(s => s.GetFile(videoId)).ReturnsAsync(new UploadFileEntity { Id = videoId, MediaKind = MediaKind.Video, Uploaders = new() { 999 } });
        _files.Setup(s => s.GetFile(sourceId)).ReturnsAsync(new UploadFileEntity { Id = sourceId, MediaKind = MediaKind.Photo, Uploaders = new() { OwnerId } });

        var act = () => CreateSut().Handle(new SetVideoThumbnailCommand { VideoFileId = videoId, SourceImageFileId = sourceId }, default);

        await act.Should().ThrowAsync<CloudAccessDeniedException>();
    }

    [Fact]
    public async Task Handle_WrongMediaKind_Throws()
    {
        var videoId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        // "Видео" на деле фото — источник смены превью некорректен.
        _files.Setup(s => s.GetFile(videoId)).ReturnsAsync(new UploadFileEntity { Id = videoId, MediaKind = MediaKind.Photo, Uploaders = new() { OwnerId } });
        _files.Setup(s => s.GetFile(sourceId)).ReturnsAsync(new UploadFileEntity { Id = sourceId, MediaKind = MediaKind.Photo, Uploaders = new() { OwnerId } });

        var act = () => CreateSut().Handle(new SetVideoThumbnailCommand { VideoFileId = videoId, SourceImageFileId = sourceId }, default);

        await act.Should().ThrowAsync<InvalidThumbnailSourceException>();
    }
}
