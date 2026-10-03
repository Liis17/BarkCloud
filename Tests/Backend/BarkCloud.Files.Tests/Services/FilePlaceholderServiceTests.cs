using BarkCloud.Files.Domain;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BarkCloud.Files.Tests.Services;

public sealed class FilePlaceholderServiceTests : IDisposable
{
    private readonly SqliteFilesContext _db = new();
    private readonly Mock<S3BucketRegistry> _registry = new(TestConfiguration.Empty());
    private readonly Mock<S3Uploader> _s3;

    public FilePlaceholderServiceTests() => _s3 = new Mock<S3Uploader>(_registry.Object);

    [Fact]
    public async Task Ensure_NewPreview_StoresColorsWithoutDownloading()
    {
        var (file, source) = await SeedPreview();
        using var image = new Image<Rgba32>(30, 20, Color.Red.ToPixel<Rgba32>());
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        var service = new FilePlaceholderService(_db.Context, _s3.Object, _registry.Object, new ImagePlaceholderSampler());

        var saved = await service.EnsureAsync(file, new Dictionary<Guid, byte[]> { [source.PreviewFileId] = stream.ToArray() });

        saved.Should().BeTrue();
        var placeholders = await new UploadedFilesStorage(_db.Context).GetPlaceholdersForFiles([file.Id]);
        placeholders[file.Id].Colors.Should().Equal(Enumerable.Repeat("#FF0000", 9));
        placeholders[file.Id].SourceFilePreviewId.Should().Be(source.Id);
        placeholders[file.Id].AspectRatio.Should().Be(1);
        _s3.Verify(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Ensure_ExistingPlaceholder_IsNotOverwrittenByWorker()
    {
        var (file, source) = await SeedPreview();
        var service = CreateSut();
        await service.EnsureAsync(file, new Dictionary<Guid, byte[]> { [source.PreviewFileId] = Bytes(Color.Red) });

        var saved = await service.EnsureAsync(file, new Dictionary<Guid, byte[]> { [source.PreviewFileId] = Bytes(Color.Blue) });

        saved.Should().BeFalse();
        (await _db.Context.FilePlaceholders.AsNoTracking().SingleAsync()).Colors
            .Should().Equal(Enumerable.Repeat("#FF0000", 9));
    }

    [Theory]
    [InlineData(128)]
    [InlineData(512)]
    [InlineData(1024)]
    public async Task Ensure_PrefersAvailableOrdinaryPreviewOverJpegView(int width)
    {
        var (file, ordinary) = await SeedPreview(width);
        _db.Context.FilePreviews.Add(new FilePreview
        {
            Id = Guid.NewGuid(), OriginalFileId = file.Id, PreviewFileId = ordinary.PreviewFileId,
            TargetWidth = 0, ActualWidth = 30, ActualHeight = 20, CreatedAt = DateTime.UtcNow
        });
        await _db.Context.SaveChangesAsync();

        await CreateSut().EnsureAsync(file, new Dictionary<Guid, byte[]> { [ordinary.PreviewFileId] = Bytes(Color.Blue) });

        var placeholder = await _db.Context.FilePlaceholders.AsNoTracking().SingleAsync();
        placeholder.SourceFilePreviewId.Should().Be(ordinary.Id);
        placeholder.Colors.Should().Equal(Enumerable.Repeat("#0000FF", 9));
    }

    [Fact]
    public async Task Ensure_SmallPhoto_UsesProvidedJpegView()
    {
        var (file, source) = await SeedPreview(0);

        await CreateSut().EnsureAsync(file, new Dictionary<Guid, byte[]> { [source.PreviewFileId] = Bytes(Color.Blue) });

        var placeholder = await _db.Context.FilePlaceholders.AsNoTracking().SingleAsync();
        placeholder.SourceFilePreviewId.Should().Be(source.Id);
        placeholder.Colors.Should().Equal(Enumerable.Repeat("#0000FF", 9));
        _s3.Verify(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Ensure_MissingSmallPreviewBlob_UsesNextAvailablePreview()
    {
        var (file, smallest) = await SeedPreview();
        var available = new FilePreview
        {
            Id = Guid.NewGuid(), OriginalFileId = file.Id, PreviewFileId = smallest.PreviewFileId,
            TargetWidth = 512, ActualWidth = 30, ActualHeight = 20, CreatedAt = DateTime.UtcNow
        };
        _db.Context.FilePreviews.Add(available);
        smallest.PreviewFileId = Guid.NewGuid();
        await _db.Context.SaveChangesAsync();

        (await CreateSut().EnsureAsync(file, new Dictionary<Guid, byte[]> { [available.PreviewFileId] = Bytes(Color.Blue) })).Should().BeTrue();

        (await _db.Context.FilePlaceholders.AsNoTracking().SingleAsync()).SourceFilePreviewId.Should().Be(available.Id);
    }

    [Fact]
    public async Task Ensure_Backfill_ReadsPreviewStorageProfileOnly()
    {
        var (file, source) = await SeedPreview();
        _registry.Setup(r => r.ResolveReadProfileId(It.Is<UploadFile>(f => f.Id == source.PreviewFileId)))
            .Returns("previews");
        _s3.Setup(s => s.DownloadAsync("previews", source.PreviewFileId.ToString(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Bytes(Color.Red)));

        (await CreateSut().EnsureAsync(file)).Should().BeTrue();

        _s3.Verify(s => s.DownloadAsync("previews", source.PreviewFileId.ToString(), It.IsAny<CancellationToken>()), Times.Once);
        _s3.Verify(s => s.DownloadAsync(It.IsAny<string>(), file.Id.ToString(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Ensure_SourceDeletedDuringDownload_DiscardsResult()
    {
        var (file, source) = await SeedPreview();
        _s3.Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await _db.Context.FilePreviews.Where(p => p.Id == source.Id).ExecuteDeleteAsync();
                return new MemoryStream(Bytes(Color.Red));
            });

        (await CreateSut().EnsureAsync(file)).Should().BeFalse();

        (await _db.Context.FilePlaceholders.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Ensure_ColorFailure_DoesNotFailUploadPreviewPipeline()
    {
        var (file, source) = await SeedPreview();
        var persistence = new PreviewPersistenceService(
            new UploadedFilesStorage(_db.Context), Mock.Of<IFileHashesStorage>(), _s3.Object, _db.Context,
            NullLogger<PreviewPersistenceService>.Instance, CreateSut());

        var process = () => persistence.EnsurePlaceholderAsync(file,
            new Dictionary<Guid, byte[]> { [source.PreviewFileId] = [1, 2, 3] }, default);

        await process.Should().NotThrowAsync();
        (await _db.Context.FilePlaceholders.CountAsync()).Should().Be(0);
    }

    private FilePlaceholderService CreateSut() =>
        new(_db.Context, _s3.Object, _registry.Object, new ImagePlaceholderSampler());

    private static byte[] Bytes(Color color)
    {
        using var image = new Image<Rgba32>(30, 20, color.ToPixel<Rgba32>());
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    private async Task<(UploadFile File, FilePreview Source)> SeedPreview(int width = 128)
    {
        var file = new UploadFile
        {
            Id = Guid.NewGuid(), Type = UploadFileType.CloudFile, MediaKind = MediaKind.Photo,
            StorageProfileId = "originals", CreatedAt = DateTime.UtcNow, Uploaders = [1]
        };
        var preview = new UploadFile
        {
            Id = Guid.NewGuid(), Type = UploadFileType.CloudFile, MediaKind = MediaKind.Photo,
            StorageProfileId = "previews", CreatedAt = DateTime.UtcNow, UploadedAt = DateTime.UtcNow,
            Etag = "etag", Uploaders = [1]
        };
        var source = new FilePreview
        {
            Id = Guid.NewGuid(), OriginalFileId = file.Id, PreviewFileId = preview.Id,
            TargetWidth = width, ActualWidth = 30, ActualHeight = 20, CreatedAt = DateTime.UtcNow
        };
        _db.Context.UploadedFiles.AddRange(file, preview);
        _db.Context.FilePreviews.Add(source);
        await _db.Context.SaveChangesAsync();
        return (file, source);
    }

    public void Dispose() => _db.Dispose();
}
