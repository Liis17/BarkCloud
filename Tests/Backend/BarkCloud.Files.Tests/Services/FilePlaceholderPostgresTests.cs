using BarkCloud.Files.Domain;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BarkCloud.Files.Tests.Services;

public sealed class FilePlaceholderPostgresTests
{
    [PostgresFact]
    public async Task Ensure_ConcurrentWriters_InsertOneNativeArray()
    {
        await using var db = await PostgresFilesDatabase.CreateAsync();
        await using var first = db.CreateContext();
        var file = NewFile();
        var source = AddPreview(first, file);
        await first.SaveChangesAsync();
        await using var second = db.CreateContext();
        var previews = new Dictionary<Guid, byte[]> { [source.PreviewFileId] = Bytes(Color.Red) };

        var writes = await Task.WhenAll(Service(first).EnsureAsync(file, previews), Service(second).EnsureAsync(file, previews));

        writes.Count(saved => saved).Should().Be(1);
        var row = await first.FilePlaceholders.AsNoTracking().SingleAsync();
        row.SourceFilePreviewId.Should().Be(source.Id);
        row.Colors.Should().Equal(Enumerable.Repeat("#FF0000", 9));
        (await first.Database.SqlQueryRaw<string>("SELECT pg_typeof(\"Colors\")::text AS \"Value\" FROM \"FilePlaceholders\"")
            .SingleAsync()).Should().Be("text[]");
    }

    [PostgresFact]
    public async Task Placeholder_RequiresNineColors_AndCascadesWithOriginal()
    {
        await using var db = await PostgresFilesDatabase.CreateAsync();
        await using var context = db.CreateContext();
        var file = NewFile();
        var source = AddPreview(context, file);
        await context.SaveChangesAsync();
        await Service(context).EnsureAsync(file, new Dictionary<Guid, byte[]> { [source.PreviewFileId] = Bytes(Color.Red) });

        var invalid = () => context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"FilePlaceholders\" SET \"Colors\" = {new[] { "#FF0000" }} WHERE \"FileId\" = {file.Id}");
        (await invalid.Should().ThrowAsync<PostgresException>()).Which.SqlState
            .Should().Be(PostgresErrorCodes.CheckViolation);

        context.CloudFileEntries.Add(new CloudFileEntry
        {
            Id = Guid.NewGuid(), FileId = file.Id, OwnerId = 1, Name = "photo.jpg",
            CreatedAt = DateTime.UtcNow, DeletedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        (await context.FilePlaceholders.CountAsync()).Should().Be(1, "корзина сохраняет цвета");

        await context.UploadedFiles.Where(f => f.Id == file.Id).ExecuteDeleteAsync();
        (await context.FilePlaceholders.CountAsync()).Should().Be(0);
    }

    [PostgresFact]
    public async Task ChangedVideoCover_DeletesOldColorsAndUsesNewPreviewBytes()
    {
        await using var db = await PostgresFilesDatabase.CreateAsync();
        await using var context = db.CreateContext();
        var file = NewFile(MediaKind.Video);
        var oldSource = AddPreview(context, file);
        await context.SaveChangesAsync();
        await Service(context).EnsureAsync(file, new Dictionary<Guid, byte[]> { [oldSource.PreviewFileId] = Bytes(Color.Red) });

        await context.FilePreviews.Where(p => p.Id == oldSource.Id).ExecuteDeleteAsync();
        (await context.FilePlaceholders.CountAsync()).Should().Be(0);
        var s3 = MockS3();
        s3.Setup(s => s.UploadAsync("previews", It.IsAny<string>(), It.IsAny<Stream>(), "image/jpeg")).ReturnsAsync("etag");
        var persistence = new PreviewPersistenceService(
            new UploadedFilesStorage(context), Mock.Of<IFileHashesStorage>(), s3.Object, context,
            NullLogger<PreviewPersistenceService>.Instance, Service(context, s3));

        await persistence.PersistPreviewsAsync(file,
            [new MultiPreviewItem(128, 30, 20, Bytes(Color.Blue))], "previews", default);

        var row = await context.FilePlaceholders.AsNoTracking().SingleAsync();
        row.SourceFilePreviewId.Should().NotBe(oldSource.Id);
        s3.Verify(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        row.Colors.Should().Equal(Enumerable.Repeat("#0000FF", 9));
        row.AspectRatio.Should().Be(1.5f);
    }

    [PostgresFact]
    public async Task OldWorkerFinishingAfterCoverReplacement_DoesNotOverwritePipeline()
    {
        await using var db = await PostgresFilesDatabase.CreateAsync();
        await using var workerContext = db.CreateContext();
        var file = NewFile(MediaKind.Video);
        var oldSource = AddPreview(workerContext, file);
        await workerContext.SaveChangesAsync();
        var s3 = MockS3();
        s3.Setup(s => s.DownloadAsync("previews", oldSource.PreviewFileId.ToString(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await using var pipelineContext = db.CreateContext();
                await pipelineContext.FilePreviews.Where(p => p.Id == oldSource.Id).ExecuteDeleteAsync();
                var currentFile = await pipelineContext.UploadedFiles.SingleAsync(f => f.Id == file.Id);
                var newSource = AddPreview(pipelineContext, currentFile);
                await pipelineContext.SaveChangesAsync();
                await Service(pipelineContext).EnsureAsync(currentFile,
                    new Dictionary<Guid, byte[]> { [newSource.PreviewFileId] = Bytes(Color.Blue) }, overwriteExisting: true);
                return new MemoryStream(Bytes(Color.Red));
            });

        (await Service(workerContext, s3).EnsureAsync(file)).Should().BeFalse();

        (await workerContext.FilePlaceholders.AsNoTracking().SingleAsync()).Colors
            .Should().Equal(Enumerable.Repeat("#0000FF", 9));
    }

    [PostgresFact]
    public async Task OldPipelineStartingAfterCoverReplacement_DiscardsItsProvidedBytes()
    {
        await using var db = await PostgresFilesDatabase.CreateAsync();
        await using var context = db.CreateContext();
        var file = NewFile(MediaKind.Video);
        var oldSource = AddPreview(context, file);
        await context.SaveChangesAsync();
        var oldBytes = new Dictionary<Guid, byte[]> { [oldSource.PreviewFileId] = Bytes(Color.Red) };
        await context.FilePreviews.Where(p => p.Id == oldSource.Id).ExecuteDeleteAsync();
        var newSource = AddPreview(context, file);
        await context.SaveChangesAsync();
        var newBytes = new Dictionary<Guid, byte[]> { [newSource.PreviewFileId] = Bytes(Color.Blue) };
        var s3 = MockS3();
        var service = Service(context, s3);

        (await service.EnsureAsync(file, oldBytes, overwriteExisting: true)).Should().BeFalse();
        (await context.FilePlaceholders.CountAsync()).Should().Be(0);
        (await service.EnsureAsync(file, newBytes, overwriteExisting: true)).Should().BeTrue();
        (await service.EnsureAsync(file, oldBytes, overwriteExisting: true)).Should().BeFalse();

        var row = await context.FilePlaceholders.AsNoTracking().SingleAsync();
        row.SourceFilePreviewId.Should().Be(newSource.Id);
        row.Colors.Should().Equal(Enumerable.Repeat("#0000FF", 9));
        s3.Verify(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [PostgresFact]
    public async Task Backfill_BatchesAndRetriesMissingSourcesAndErrors_WithoutProcessingAvatarsOrUnreadyFiles()
    {
        await using var db = await PostgresFilesDatabase.CreateAsync();
        var bytesByPreview = new Dictionary<string, byte[]>();
        await using var context = db.CreateContext();
        for (var i = 0; i < 201; i++)
        {
            var source = AddPreview(context, NewFile(i % 2 == 0 ? MediaKind.Photo : MediaKind.Video));
            bytesByPreview[source.PreviewFileId.ToString()] = Bytes(Color.Red);
        }
        var missing = NewFile();
        context.UploadedFiles.Add(missing);
        var broken = AddPreview(context, NewFile());
        bytesByPreview[broken.PreviewFileId.ToString()] = [1, 2, 3];
        var avatar = NewFile();
        avatar.Type = UploadFileType.UserAvatar;
        AddPreview(context, avatar);
        var unready = NewFile();
        unready.UploadedAt = null;
        AddPreview(context, unready);
        await context.SaveChangesAsync();
        var s3 = MockS3();
        s3.Setup(s => s.DownloadAsync("previews", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string key, CancellationToken _) => Task.FromResult<Stream>(new MemoryStream(bytesByPreview[key])));
        var scopes = 0;
        using var provider = new ServiceCollection()
            .AddScoped(_ => { scopes++; return db.CreateContext(); })
            .AddScoped(sp => Service(sp.GetRequiredService<FilesContext>(), s3))
            .BuildServiceProvider();
        var worker = new FilePlaceholderBackfillService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<FilePlaceholderBackfillService>.Instance, TimeProvider.System);

        await worker.RunPassAsync();

        (await context.FilePlaceholders.CountAsync()).Should().Be(201);
        scopes.Should().BeGreaterThan(203, "каждый файл обрабатывается в собственном scope, порции продолжаются после 200");
        var lateSource = AddPreview(context, missing, targetWidth: 0);
        bytesByPreview[lateSource.PreviewFileId.ToString()] = Bytes(Color.Blue);
        bytesByPreview[broken.PreviewFileId.ToString()] = Bytes(Color.Blue);
        await context.SaveChangesAsync();
        s3.Invocations.Clear();

        await worker.RunPassAsync();

        (await context.FilePlaceholders.CountAsync()).Should().Be(203);
        s3.Verify(s => s.DownloadAsync("previews", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        (await context.FilePlaceholders.AnyAsync(p => p.FileId == avatar.Id || p.FileId == unready.Id)).Should().BeFalse();
        await worker.RunPassAsync();
        s3.Verify(s => s.DownloadAsync("previews", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task BackgroundStart_DoesNotWaitForFirstPass_AndStopsOnCancellation()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var worker = new FilePlaceholderBackfillService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<FilePlaceholderBackfillService>.Instance, TimeProvider.System);

        await worker.StartAsync(default).WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(2));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var pass = () => worker.RunPassAsync(cancelled.Token);
        await pass.Should().ThrowAsync<OperationCanceledException>();
    }

    private static UploadFile NewFile(MediaKind kind = MediaKind.Photo) => new()
    {
        Id = Guid.NewGuid(), Type = UploadFileType.CloudFile, MediaKind = kind, Uploaders = [1],
        CreatedAt = DateTime.UtcNow, UploadedAt = DateTime.UtcNow, Etag = "etag", StorageProfileId = "originals"
    };

    private static FilePreview AddPreview(FilesContext context, UploadFile original, int targetWidth = 128)
    {
        if (context.Entry(original).State == EntityState.Detached)
            context.UploadedFiles.Add(original);
        var preview = NewFile();
        preview.StorageProfileId = "previews";
        context.UploadedFiles.Add(preview);
        var source = new FilePreview
        {
            Id = Guid.NewGuid(), OriginalFileId = original.Id, PreviewFileId = preview.Id,
            TargetWidth = targetWidth, ActualWidth = 30, ActualHeight = 20, CreatedAt = DateTime.UtcNow
        };
        context.FilePreviews.Add(source);
        return source;
    }

    private static byte[] Bytes(Color color)
    {
        using var image = new Image<Rgba32>(30, 20, color.ToPixel<Rgba32>());
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    private static Mock<S3Uploader> MockS3() => new(new S3BucketRegistry(TestConfiguration.Empty()));

    private static FilePlaceholderService Service(FilesContext context, Mock<S3Uploader>? s3 = null) =>
        new(context, (s3 ?? MockS3()).Object, new S3BucketRegistry(TestConfiguration.Empty()), new ImagePlaceholderSampler());
}
