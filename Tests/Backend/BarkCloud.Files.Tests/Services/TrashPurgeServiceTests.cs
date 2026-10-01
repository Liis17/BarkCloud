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

    private CloudFileEntry SeedEntry(Guid fileId, bool deleted, DateTime? purgeAt = null)
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
            PurgeAt = deleted ? purgeAt : null,
        };
        _db.Context.CloudFileEntries.Add(entry);
        return entry;
    }

    /// <summary>Привязки файла владельца (избранное, альбом, публичная ссылка), которые чистит purge.</summary>
    private void SeedDependencies(Guid fileId)
    {
        _db.Context.FavoriteFiles.Add(new FavoriteFile
        {
            Id = Guid.NewGuid(), OwnerId = OwnerId, FileId = fileId, CreatedAt = DateTime.UtcNow
        });
        _db.Context.AlbumItems.Add(new AlbumItem
        {
            Id = Guid.NewGuid(), AlbumId = Guid.NewGuid(), OwnerId = OwnerId, FileId = fileId, AddedAt = DateTime.UtcNow
        });
        _db.Context.ShareLinks.Add(new ShareLink
        {
            Id = Guid.NewGuid(), OwnerId = OwnerId, FileId = fileId, Token = Guid.NewGuid().ToString("N"),
            Name = "a.jpg", CreatedAt = DateTime.UtcNow
        });
    }

    private async Task<int> DependencyCountAsync(Guid fileId) =>
        await _db.Context.FavoriteFiles.CountAsync(f => f.FileId == fileId)
        + await _db.Context.AlbumItems.CountAsync(a => a.FileId == fileId)
        + await _db.Context.ShareLinks.CountAsync(s => s.FileId == fileId);

    /// <summary>
    /// Параллельное действие пользователя из другого контекста («другое устройство»): меняет запись
    /// в БД, пока у воркера на руках уже устаревший снимок.
    /// </summary>
    private async Task RestoreConcurrentlyAsync(Guid entryId)
    {
        using var other = _db.CreateAdditionalContext();
        await other.CloudFileEntries
            .Where(e => e.Id == entryId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.IsDeleted, false)
                .SetProperty(e => e.DeletedAt, (DateTime?)null)
                .SetProperty(e => e.PurgeAt, (DateTime?)null));
    }

    private async Task ReTrashConcurrentlyAsync(Guid entryId, DateTime newPurgeAt)
    {
        using var other = _db.CreateAdditionalContext();
        await other.CloudFileEntries
            .Where(e => e.Id == entryId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.PurgeAt, (DateTime?)newPurgeAt));
    }

    /// <summary>Выборка воркера: tracked-снимок просроченных записей.</summary>
    private Task<List<CloudFileEntry>> SelectExpiredAsync(DateTime now) =>
        _db.Context.CloudFileEntries.Where(e => e.IsDeleted && e.PurgeAt != null && e.PurgeAt <= now).ToListAsync();

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

    [Fact]
    public async Task PurgeExpiredEntries_ExpiredEntry_IsPurgedWithDependenciesAndBlob()
    {
        var now = DateTime.UtcNow;
        var fileId = Guid.NewGuid();
        _db.Context.UploadedFiles.Add(Blob(fileId, OwnerId));
        SeedEntry(fileId, deleted: true, purgeAt: now.AddHours(-1));
        SeedDependencies(fileId);
        await _db.Context.SaveChangesAsync();

        var batch = await SelectExpiredAsync(now);
        var result = await CreateSut().PurgeExpiredEntriesAsync(batch, now, default);

        _db.Context.ChangeTracker.Clear();
        result.Entries.Should().Be(1);
        result.Blobs.Should().Be(1);
        (await _db.Context.CloudFileEntries.AnyAsync()).Should().BeFalse();
        (await DependencyCountAsync(fileId)).Should().Be(0);
        (await _db.Context.UploadedFiles.FindAsync(fileId)).Should().BeNull();
        _deletedKeys.Should().Contain(fileId.ToString());
    }

    [Fact]
    public async Task PurgeExpiredEntries_RestoredAfterSelection_KeepsEntryDependenciesAndBlob()
    {
        // F06: воркер выбрал просроченную запись, пользователь восстановил её, затем воркер удаляет.
        var now = DateTime.UtcNow;
        var fileId = Guid.NewGuid();
        _db.Context.UploadedFiles.Add(Blob(fileId, OwnerId));
        var entry = SeedEntry(fileId, deleted: true, purgeAt: now.AddHours(-1));
        SeedDependencies(fileId);
        await _db.Context.SaveChangesAsync();

        var batch = await SelectExpiredAsync(now);
        await RestoreConcurrentlyAsync(entry.Id);

        var result = await CreateSut().PurgeExpiredEntriesAsync(batch, now, default);

        _db.Context.ChangeTracker.Clear();
        result.Should().Be(new TrashPurgeResult(0, 0));
        var restored = await _db.Context.CloudFileEntries.SingleAsync();
        restored.IsDeleted.Should().BeFalse("восстановленный файл не должен удаляться воркером");
        (await DependencyCountAsync(fileId)).Should().Be(3, "избранное, альбом и ссылка восстановленного файла сохраняются");
        var blob = await _db.Context.UploadedFiles.FindAsync(fileId);
        blob.Should().NotBeNull();
        blob!.Uploaders.Should().Contain(OwnerId);
        _deletedKeys.Should().BeEmpty("оригинал в S3 не удаляется");
    }

    [Fact]
    public async Task PurgeExpiredEntries_ReTrashedWithLaterPurgeAt_IsNotPurged()
    {
        // Запись восстановили и снова удалили: PurgeAt уже в будущем, срок хранения начался заново.
        var now = DateTime.UtcNow;
        var fileId = Guid.NewGuid();
        _db.Context.UploadedFiles.Add(Blob(fileId, OwnerId));
        var entry = SeedEntry(fileId, deleted: true, purgeAt: now.AddHours(-1));
        await _db.Context.SaveChangesAsync();

        var batch = await SelectExpiredAsync(now);
        await ReTrashConcurrentlyAsync(entry.Id, now.AddDays(14));

        var result = await CreateSut().PurgeExpiredEntriesAsync(batch, now, default);

        _db.Context.ChangeTracker.Clear();
        result.Entries.Should().Be(0);
        (await _db.Context.CloudFileEntries.SingleAsync()).IsDeleted.Should().BeTrue();
        (await _db.Context.UploadedFiles.FindAsync(fileId)).Should().NotBeNull();
        _deletedKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task PurgeExpiredEntries_MixedBatch_PurgesOnlyStillExpired()
    {
        var now = DateTime.UtcNow;
        var restoredFile = Guid.NewGuid();
        var expiredFile = Guid.NewGuid();
        _db.Context.UploadedFiles.AddRange(Blob(restoredFile, OwnerId), Blob(expiredFile, OwnerId));
        var restoredEntry = SeedEntry(restoredFile, deleted: true, purgeAt: now.AddHours(-2));
        SeedEntry(expiredFile, deleted: true, purgeAt: now.AddHours(-1));
        SeedDependencies(restoredFile);
        SeedDependencies(expiredFile);
        await _db.Context.SaveChangesAsync();

        var batch = await SelectExpiredAsync(now);
        await RestoreConcurrentlyAsync(restoredEntry.Id);

        var result = await CreateSut().PurgeExpiredEntriesAsync(batch, now, default);

        _db.Context.ChangeTracker.Clear();
        result.Entries.Should().Be(1);
        (await _db.Context.CloudFileEntries.SingleAsync()).FileId.Should().Be(restoredFile);
        (await DependencyCountAsync(restoredFile)).Should().Be(3);
        (await DependencyCountAsync(expiredFile)).Should().Be(0);
        (await _db.Context.UploadedFiles.FindAsync(restoredFile)).Should().NotBeNull();
        (await _db.Context.UploadedFiles.FindAsync(expiredFile)).Should().BeNull();
        _deletedKeys.Should().ContainSingle().Which.Should().Be(expiredFile.ToString());
    }

    [Fact]
    public async Task PurgeEntries_AlreadyRestored_DoesNothing()
    {
        // Ручное «Удалить навсегда»: запись успели восстановить с другого устройства.
        var fileId = Guid.NewGuid();
        _db.Context.UploadedFiles.Add(Blob(fileId, OwnerId));
        var entry = SeedEntry(fileId, deleted: true, purgeAt: DateTime.UtcNow.AddDays(10));
        SeedDependencies(fileId);
        await _db.Context.SaveChangesAsync();

        await RestoreConcurrentlyAsync(entry.Id);
        var result = await CreateSut().PurgeEntriesAsync(new[] { entry }, default);

        _db.Context.ChangeTracker.Clear();
        result.Entries.Should().Be(0);
        (await _db.Context.CloudFileEntries.SingleAsync()).IsDeleted.Should().BeFalse();
        (await DependencyCountAsync(fileId)).Should().Be(3);
        (await _db.Context.UploadedFiles.FindAsync(fileId)).Should().NotBeNull();
        _deletedKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task PurgeEntries_FileHasLiveEntry_KeepsDependenciesAndOwnership()
    {
        // Файл удалили, загрузили повторно (живая запись на тот же блоб) — purge старой записи в
        // корзине не должен стирать избранное/альбомы/ссылки живой записи и снимать владельца.
        var fileId = Guid.NewGuid();
        _db.Context.UploadedFiles.Add(Blob(fileId, OwnerId));
        var trashed = SeedEntry(fileId, deleted: true);
        SeedEntry(fileId, deleted: false);
        SeedDependencies(fileId);
        await _db.Context.SaveChangesAsync();

        var result = await CreateSut().PurgeEntriesAsync(new[] { trashed }, default);

        _db.Context.ChangeTracker.Clear();
        result.Entries.Should().Be(1);
        result.Blobs.Should().Be(0);
        var remaining = await _db.Context.CloudFileEntries.SingleAsync();
        remaining.IsDeleted.Should().BeFalse();
        (await DependencyCountAsync(fileId)).Should().Be(3);
        var blob = await _db.Context.UploadedFiles.FindAsync(fileId);
        blob!.Uploaders.Should().Contain(OwnerId);
        _deletedKeys.Should().BeEmpty();
    }

    public void Dispose() => _db.Dispose();
}
