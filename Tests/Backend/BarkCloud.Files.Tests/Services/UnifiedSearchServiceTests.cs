using BarkCloud.Files.Domain;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.Proto.Files;

using Grpc.Core;

using MediaKind = BarkCloud.Files.Domain.MediaKind;
using UploadFileType = BarkCloud.Files.Domain.UploadFileType;

namespace BarkCloud.Files.Tests.Services;

public class UnifiedSearchServiceTests : IDisposable
{
    private const long OwnerId = 42;
    private readonly SqliteFilesContext _db = new();

    [Fact]
    public async Task GetFileSearchMetadata_FileIsNotReady_ReturnsNotFound()
    {
        var fileId = Guid.NewGuid();
        _db.Context.UploadedFiles.Add(new UploadFile
        {
            Id = fileId,
            Uploaders = new List<long> { OwnerId },
            Type = UploadFileType.CloudFile,
            Filename = "processing.pdf"
        });
        await _db.Context.SaveChangesAsync();
        var service = new UnifiedSearchService(
            _db.Context,
            UserContextFactory.Create(OwnerId),
            new RunSettings { Host = "http://localhost", Http1Port = 7026 },
            TestConfiguration.Empty());

        var act = () => service.GetFileSearchMetadata(fileId, default);

        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Theory]
    [InlineData(SearchHitKind.Photo, MediaKind.Photo)]
    [InlineData(SearchHitKind.Video, MediaKind.Video)]
    [InlineData(SearchHitKind.Track, MediaKind.Audio)]
    public async Task ResolveHit_OwnedMediaByFileId_ReturnsHit(SearchHitKind kind, MediaKind mediaKind)
    {
        var (fileId, entryId) = await AddCloudFile(mediaKind);

        var hit = await CreateService().ResolveHit(new SearchHitReference { Kind = kind, Id = fileId.ToString() }, default);

        hit.Kind.Should().Be(kind);
        hit.FileId.Should().Be(fileId.ToString());
        hit.EntryId.Should().Be(entryId.ToString());
    }

    [Fact]
    public async Task ResolveHit_OwnedMediaByEntryId_ReturnsHit()
    {
        var (fileId, entryId) = await AddCloudFile(MediaKind.Photo);

        var hit = await CreateService().ResolveHit(new SearchHitReference { Kind = SearchHitKind.Photo, Id = entryId.ToString() }, default);

        hit.FileId.Should().Be(fileId.ToString());
    }

    [Fact]
    public async Task ResolveHit_SmallPhoto_ReturnsJpegViewAndPlaceholder()
    {
        var (fileId, entryId) = await AddCloudFile(MediaKind.Photo);
        var (previewId, _) = await AddCloudFile(MediaKind.Photo);
        var sourceId = Guid.NewGuid();
        _db.Context.FilePreviews.Add(new FilePreview
        {
            Id = sourceId, OriginalFileId = fileId, PreviewFileId = previewId,
            TargetWidth = 0, ActualWidth = 20, ActualHeight = 10, CreatedAt = DateTime.UtcNow
        });
        _db.Context.FilePlaceholders.Add(new FilePlaceholder
        {
            FileId = fileId, SourceFilePreviewId = sourceId,
            Colors = Enumerable.Repeat("#112233", 9).ToArray(), AspectRatio = 1
        });
        await _db.Context.SaveChangesAsync();

        var hit = await CreateService().ResolveHit(new SearchHitReference { Kind = SearchHitKind.Photo, Id = entryId.ToString() }, default);

        hit.PreviewUrl.Should().Be($"http://localhost:7026/download/{previewId}");
        hit.Placeholder.Colors.Should().Equal(Enumerable.Repeat("#112233", 9));
        hit.Placeholder.AspectRatio.Should().Be(1);
    }

    [Fact]
    public async Task ResolveHit_UnknownId_ReturnsNotFound()
    {
        await AddCloudFile(MediaKind.Photo);

        var act = () => CreateService().ResolveHit(new SearchHitReference { Kind = SearchHitKind.Photo, Id = Guid.NewGuid().ToString() }, default);

        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    private async Task<(Guid FileId, Guid EntryId)> AddCloudFile(MediaKind mediaKind)
    {
        var fileId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        _db.Context.UploadedFiles.Add(new UploadFile
        {
            Id = fileId,
            Uploaders = new List<long> { OwnerId },
            Type = UploadFileType.CloudFile,
            MediaKind = mediaKind,
            Filename = "cat.bin",
            Etag = "etag",
            UploadedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        });
        _db.Context.CloudFileEntries.Add(new CloudFileEntry
        {
            Id = entryId,
            OwnerId = OwnerId,
            DirectoryId = Guid.NewGuid(),
            FileId = fileId,
            Name = "cat.bin",
            CreatedAt = DateTime.UtcNow
        });
        await _db.Context.SaveChangesAsync();
        return (fileId, entryId);
    }

    private UnifiedSearchService CreateService() => new(
        _db.Context,
        UserContextFactory.Create(OwnerId),
        new RunSettings { Host = "http://localhost", Http1Port = 7026 },
        TestConfiguration.Empty());

    public void Dispose() => _db.Dispose();
}
