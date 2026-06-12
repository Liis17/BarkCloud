using BarkCloud.Files.Domain;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Services;

/// <summary>
/// Интеграционные тесты окончательной зачистки корзины поверх реального <see cref="FilesContext"/>
/// (SQLite in-memory). Покрывают дедупликацию превью: один превью-блоб может быть привязан к
/// нескольким оригиналам, и удаление одного из них не должно убивать общий превью.
/// </summary>
public class TrashPurgeServiceTests : IDisposable
{
    private const long OwnerId = 1;

    private readonly SqliteFilesContext _db = new();
    private readonly Mock<S3BucketRegistry> _bucketRegistry;
    private readonly Mock<S3Uploader> _s3;
    private readonly Mock<IFileHashesStorage> _hashes = new();
    private readonly List<string> _deletedKeys = new();

    public TrashPurgeServiceTests()
    {
        _bucketRegistry = new Mock<S3BucketRegistry>(TestConfiguration.Empty()) { CallBase = false };
        _bucketRegistry.Setup(r => r.GetBucketName(It.IsAny<UploadFileType>())).Returns("cloud-files");

        _s3 = new Mock<S3Uploader>(_bucketRegistry.Object) { CallBase = false };
        _s3.Setup(u => u.DeleteAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((_, key) => _deletedKeys.Add(key))
            .Returns(Task.CompletedTask);

        _hashes.Setup(h => h.DeleteHashByFileId(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
    }

    private TrashPurgeService CreateSut() => new(
        _db.Context, _s3.Object, _bucketRegistry.Object, _hashes.Object,
        NullLogger<TrashPurgeService>.Instance);

    private static UploadFile Blob(Guid id, params long[] uploaders) => new()
    {
        Id = id,
        Uploaders = uploaders.ToList(),
        Type = UploadFileType.CloudFile,
        MediaKind = MediaKind.Photo,
        CreatedAt = DateTime.UtcNow,
        UploadedAt = DateTime.UtcNow,
    };

    private CloudFileEntry SeedEntry(Guid fileId, bool deleted)
    {
        var entry = new CloudFileEntry
        {
            Id = Guid.NewGuid(),
            OwnerId = OwnerId,
            FileId = fileId,
            Name = $"{fileId}.jpg",
            IsDeleted = deleted,
            CreatedAt = DateTime.UtcNow,
            DeletedAt = deleted ? DateTime.UtcNow : null,
        };
        _db.Context.CloudFileEntries.Add(entry);
        return entry;
    }

    [Fact]
    public async Task PurgeEntries_SharedPreview_KeepsPreviewOfRemainingFile()
    {
        // Два оригинала владельца делят один дедуплицированный превью-блоб.
        var original1 = Guid.NewGuid();
        var original2 = Guid.NewGuid();
        var sharedPreview = Guid.NewGuid();

        _db.Context.UploadedFiles.AddRange(
            Blob(original1, OwnerId),
            Blob(original2, OwnerId),
            Blob(sharedPreview, OwnerId));
        _db.Context.FilePreviews.AddRange(
            new FilePreview { Id = Guid.NewGuid(), OriginalFileId = original1, PreviewFileId = sharedPreview, TargetWidth = 128 },
            new FilePreview { Id = Guid.NewGuid(), OriginalFileId = original2, PreviewFileId = sharedPreview, TargetWidth = 128 });
        var deletedEntry = SeedEntry(original1, deleted: true);
        SeedEntry(original2, deleted: false);
        await _db.Context.SaveChangesAsync();

        await CreateSut().PurgeEntriesAsync(new[] { deletedEntry }, default);

        _db.Context.ChangeTracker.Clear();

        // Общий превью-блоб должен уцелеть — на него ещё ссылается оставшийся original2.
        var preview = await _db.Context.UploadedFiles.FindAsync(sharedPreview);
        preview.Should().NotBeNull("общий превью-блоб не должен удаляться, пока на него ссылается оставшийся файл");
        preview!.Uploaders.Should().Contain(OwnerId);

        // Связка оставшегося файла с превью цела, S3-объект превью не трогали.
        var remainingLink = await _db.Context.FilePreviews
            .FirstOrDefaultAsync(p => p.OriginalFileId == original2 && p.PreviewFileId == sharedPreview);
        remainingLink.Should().NotBeNull("превью оставшегося файла не должно осиротеть");
        _deletedKeys.Should().NotContain(sharedPreview.ToString());

        // Удалённый оригинал и его связка снесены.
        (await _db.Context.UploadedFiles.FindAsync(original1)).Should().BeNull();
        _deletedKeys.Should().Contain(original1.ToString());
    }

    [Fact]
    public async Task PurgeEntries_PrivatePreview_IsPurgedWithOriginal()
    {
        // Превью принадлежит единственному (удаляемому) оригиналу — должно физически удалиться.
        var original = Guid.NewGuid();
        var preview = Guid.NewGuid();

        _db.Context.UploadedFiles.AddRange(Blob(original, OwnerId), Blob(preview, OwnerId));
        _db.Context.FilePreviews.Add(new FilePreview
        {
            Id = Guid.NewGuid(),
            OriginalFileId = original,
            PreviewFileId = preview,
            TargetWidth = 128,
        });
        var deletedEntry = SeedEntry(original, deleted: true);
        await _db.Context.SaveChangesAsync();

        await CreateSut().PurgeEntriesAsync(new[] { deletedEntry }, default);

        _db.Context.ChangeTracker.Clear();

        (await _db.Context.UploadedFiles.FindAsync(preview)).Should().BeNull("приватное превью осиротевает вместе с оригиналом");
        _deletedKeys.Should().Contain(preview.ToString());
        (await _db.Context.FilePreviews.AnyAsync(p => p.PreviewFileId == preview)).Should().BeFalse();
    }

    public void Dispose() => _db.Dispose();
}
