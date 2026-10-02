using System.Data.Common;
using System.Security.Cryptography;

using BarkCloud.Files.Domain;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Files.Tests.Persistence;

/// <summary>
/// F15: конкурентные изменения списка владельцев <see cref="UploadFile.Uploaders"/> на реальном PostgreSQL.
/// Сценарии с общим превью останавливают одну из сторон на барьере (перед записью или в S3), пока вторая
/// выполняется целиком: без атомарных операций и блокировок строки блоба вторая сторона закончила бы
/// работу по устаревшему состоянию. С блокировкой она вместо этого ждёт — поэтому ожидание ограничено
/// таймаутом, а не бесконечно.
/// </summary>
public sealed class UploadersConcurrencyPostgresTests
{
    private const string Profile = "universal-v1";
    private const long Owner = 5;
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    [PostgresFact]
    public async Task ConcurrentAddUploader_DifferentUsers_KeepsAllOwners()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();

        for (var round = 0; round < 5; round++)
        {
            var fileId = await Seed(database, Blob(1));
            var users = Enumerable.Range(2, 16).Select(x => (long)x).ToList();
            var start = new TaskCompletionSource();

            var tasks = users.Select(user => Task.Run(async () =>
            {
                await using var context = database.CreateContext();
                await context.Database.OpenConnectionAsync();
                await start.Task;
                await new UploadedFilesStorage(context).AddUploaderToFile(fileId, user);
            })).ToList();
            start.SetResult();
            await Task.WhenAll(tasks).WaitAsync(Limit);

            (await Uploaders(database, fileId)).Should().BeEquivalentTo(new long[] { 1 }.Concat(users));
        }
    }

    [PostgresFact]
    public async Task ConcurrentAddAndRemoveUploader_KeepsBothChanges()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();

        for (var round = 0; round < 20; round++)
        {
            var fileId = await Seed(database, Blob(1));
            var start = new TaskCompletionSource();

            var add = Task.Run(async () =>
            {
                await using var context = database.CreateContext();
                await context.Database.OpenConnectionAsync();
                await start.Task;
                await new UploadedFilesStorage(context).AddUploaderToFile(fileId, 2);
            });
            var remove = Task.Run(async () =>
            {
                await using var context = database.CreateContext();
                await context.Database.OpenConnectionAsync();
                await start.Task;
                await new UploadedFilesStorage(context).RemoveUploaderFromFile(fileId, 1);
            });
            start.SetResult();
            await Task.WhenAll(add, remove).WaitAsync(Limit);

            (await Uploaders(database, fileId)).Should().BeEquivalentTo(new long[] { 2 });
        }
    }

    [PostgresFact]
    public async Task RemoveUploaderFromAll_ConcurrentWithAdd_KeepsOtherOwners()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();

        for (var round = 0; round < 20; round++)
        {
            var fileId = await Seed(database, Blob(1, 2));
            var untouched = await Seed(database, Blob(3));
            var start = new TaskCompletionSource();

            var removeAll = Task.Run(async () =>
            {
                await using var context = database.CreateContext();
                await context.Database.OpenConnectionAsync();
                await start.Task;
                return await context.RemoveUploaderFromAllAsync(1);
            });
            var add = Task.Run(async () =>
            {
                await using var context = database.CreateContext();
                await context.Database.OpenConnectionAsync();
                await start.Task;
                await new UploadedFilesStorage(context).AddUploaderToFile(fileId, 4);
            });
            start.SetResult();
            await Task.WhenAll(removeAll, add).WaitAsync(Limit);

            (await removeAll).Should().BeGreaterThanOrEqualTo(1);
            (await Uploaders(database, fileId)).Should().BeEquivalentTo(new long[] { 2, 4 });
            (await Uploaders(database, untouched)).Should().BeEquivalentTo(new long[] { 3 });
        }
    }

    [PostgresFact]
    public async Task ConcurrentPurgeOfTwoOriginalsSharingPreview_ReleasesOwnerAndDeletesPreview()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var original1 = await Seed(database, Blob(Owner));
        var original2 = await Seed(database, Blob(Owner));
        var preview = await Seed(database, Blob(Owner));
        var entry1 = await SeedDeletedEntry(database, original1);
        var entry2 = await SeedDeletedEntry(database, original2);
        await Link(database, original1, preview);
        await Link(database, original2, preview);
        var s3 = new FakeS3();

        // Оба purge доходят до решения «превью ещё нужно другому оригиналу» — каждый по устаревшему состоянию.
        using var gate = new CountdownEvent(2);
        var purge1 = Purge(database, s3, entry1, StopPoint.WaitFor(gate));
        var purge2 = Purge(database, s3, entry2, StopPoint.WaitFor(gate));
        await Task.WhenAll(purge1, purge2).WaitAsync(Limit);

        await using var context = database.CreateContext();
        (await context.UploadedFiles.AsNoTracking().Where(x => x.Uploaders.Contains(Owner)).CountAsync())
            .Should().Be(0, "владелец освобождён у всех блобов, включая общее превью");
        (await context.UploadedFiles.AsNoTracking().AnyAsync(x => x.Id == preview))
            .Should().BeFalse("на превью больше никто не ссылается — блоб должен быть удалён");
        s3.DeletedKeys.Should().Contain(preview.ToString());
    }

    [PostgresFact]
    public async Task LinkPreview_WhileOrphanBlobIsBeingDeleted_DoesNotResurrectIt()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var bytes = PreviewBytes(1);
        var orphanPreview = await Seed(database, Blob());
        await AddHash(database, orphanPreview, bytes);
        var original = await Seed(database, Blob(Owner));
        var s3 = new FakeS3 { HoldDeleteOf = orphanPreview };

        // Orphan-cleanup уже занял блоб и удаляет объект из S3.
        var cleanup = Task.Run(async () =>
        {
            await using var context = database.CreateContext();
            await CreatePurgeService(context, s3).PurgeOrphanBlobsAsync([orphanPreview], CancellationToken.None);
        });
        await s3.DeleteStarted.WaitAsync(Limit);

        // В этот момент новый оригинал находит тот же превью по хешу.
        var link = Persist(database, original, bytes);
        await Task.WhenAny(link, Task.Delay(Settle));
        s3.ReleaseDelete();
        await Task.WhenAll(cleanup, link).WaitAsync(Limit);

        await AssertPreviewIsLiveAndOwned(database, original, expectedOwner: Owner);
        await using var context = database.CreateContext();
        (await context.UploadedFiles.AsNoTracking().AnyAsync(x => x.Id == orphanPreview)).Should().BeFalse();
    }

    [PostgresFact]
    public async Task LinkPreview_WhileSameOwnerReleasesIt_KeepsPreviewForNewOriginal()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var bytes = PreviewBytes(2);
        var oldOriginal = await Seed(database, Blob(Owner));
        var preview = await Seed(database, Blob(Owner));
        await AddHash(database, preview, bytes);
        await Link(database, oldOriginal, preview);
        var oldEntry = await SeedDeletedEntry(database, oldOriginal);
        var newOriginal = await Seed(database, Blob(Owner));
        var s3 = new FakeS3();

        // Purge старого оригинала остановлен на решении, что превью больше никому не нужно.
        var reachedSave = new ManualResetEventSlim();
        var proceed = new ManualResetEventSlim();
        var purge = Purge(database, s3, oldEntry, StopPoint.Hold(reachedSave, proceed));
        reachedSave.Wait(TimeSpan.FromSeconds(3));

        // Тем временем новый оригинал того же владельца ссылается на это превью.
        var link = Persist(database, newOriginal, bytes);
        await Task.WhenAny(link, Task.Delay(Settle));
        proceed.Set();
        await Task.WhenAll(purge, link).WaitAsync(Limit);

        await AssertPreviewIsLiveAndOwned(database, newOriginal, expectedOwner: Owner);
    }

    private static async Task AssertPreviewIsLiveAndOwned(PostgresFilesDatabase database, Guid originalId, long expectedOwner)
    {
        await using var context = database.CreateContext();
        var links = await context.FilePreviews.AsNoTracking().Where(x => x.OriginalFileId == originalId).ToListAsync();
        links.Should().ContainSingle("у оригинала должно остаться своё превью");

        var blob = await context.UploadedFiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == links[0].PreviewFileId);
        blob.Should().NotBeNull("превью, на которое ссылается оригинал, не должно быть удалено");
        blob!.Uploaders.Should().Contain(expectedOwner);
    }

    private static Task Purge(PostgresFilesDatabase database, FakeS3 s3, CloudFileEntry entry, StopPoint stop) =>
        Task.Run(async () =>
        {
            await using var context = database.CreateContext(new CommitBarrier(stop));
            await CreatePurgeService(context, s3).PurgeEntriesAsync([entry], CancellationToken.None);
        });

    private static Task Persist(PostgresFilesDatabase database, Guid originalId, byte[] bytes) =>
        Task.Run(async () =>
        {
            await using var context = database.CreateContext();
            var original = await context.UploadedFiles.AsNoTracking().SingleAsync(x => x.Id == originalId);
            var service = new PreviewPersistenceService(
                new UploadedFilesStorage(context),
                new FileHashesStorage(context),
                new FakeS3().Uploader,
                context,
                NullLogger<PreviewPersistenceService>.Instance);
            await service.PersistPreviewsAsync(
                original, [new MultiPreviewItem(128, 128, 128, bytes)], Profile, CancellationToken.None);
        });

    private static TrashPurgeService CreatePurgeService(FilesContext context, FakeS3 s3) => new(
        context, s3.Uploader, s3.Registry, new FileHashesStorage(context), NullLogger<TrashPurgeService>.Instance);

    private static UploadFile Blob(params long[] uploaders) => new()
    {
        Id = Guid.NewGuid(),
        Uploaders = uploaders.ToList(),
        Type = UploadFileType.CloudFile,
        MediaKind = MediaKind.Photo,
        StorageProfileId = Profile,
        CreatedAt = DateTime.UtcNow,
        UploadedAt = DateTime.UtcNow,
        Etag = "etag",
    };

    private static async Task<Guid> Seed(PostgresFilesDatabase database, UploadFile file)
    {
        await using var context = database.CreateContext();
        context.UploadedFiles.Add(file);
        await context.SaveChangesAsync();
        return file.Id;
    }

    private static async Task<CloudFileEntry> SeedDeletedEntry(PostgresFilesDatabase database, Guid fileId)
    {
        var entry = new CloudFileEntry
        {
            Id = Guid.NewGuid(),
            OwnerId = Owner,
            DirectoryId = Guid.NewGuid(),
            FileId = fileId,
            Name = $"{fileId}.jpg",
            CreatedAt = DateTime.UtcNow,
            IsDeleted = true,
            DeletedAt = DateTime.UtcNow,
            PurgeAt = DateTime.UtcNow.AddDays(-1),
        };
        await using var context = database.CreateContext();
        context.CloudFileEntries.Add(entry);
        await context.SaveChangesAsync();
        return entry;
    }

    private static async Task Link(PostgresFilesDatabase database, Guid originalId, Guid previewId)
    {
        await using var context = database.CreateContext();
        context.FilePreviews.Add(new FilePreview
        {
            Id = Guid.NewGuid(),
            OriginalFileId = originalId,
            PreviewFileId = previewId,
            TargetWidth = 128,
            ActualWidth = 128,
            ActualHeight = 128,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    private static async Task AddHash(PostgresFilesDatabase database, Guid fileId, byte[] bytes)
    {
        await using var context = database.CreateContext();
        context.FileHashes.Add(new FileHash
        {
            FileId = fileId,
            Hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        });
        await context.SaveChangesAsync();
    }

    private static byte[] PreviewBytes(byte seed) => [seed, 1, 2, 3, 4, 5, 6, 7];

    private static async Task<List<long>> Uploaders(PostgresFilesDatabase database, Guid fileId)
    {
        await using var context = database.CreateContext();
        return (await context.UploadedFiles.AsNoTracking().SingleAsync(x => x.Id == fileId)).Uploaders;
    }

    /// <summary>Мок S3 с возможностью задержать удаление одного объекта.</summary>
    private sealed class FakeS3
    {
        private readonly TaskCompletionSource _deleteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _deleteRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _deletedKeys = [];

        public FakeS3()
        {
            var registry = new Mock<S3BucketRegistry>(TestConfiguration.Empty()) { CallBase = false };
            registry.Setup(r => r.GetBucketName(It.IsAny<UploadFileType>())).Returns("cloud-files");
            Registry = registry.Object;

            var uploader = new Mock<S3Uploader>(Registry) { CallBase = false };
            uploader.Setup(u => u.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()))
                .ReturnsAsync("etag");
            uploader.Setup(u => u.DeleteAsync(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(async (string _, string key) =>
                {
                    if (HoldDeleteOf?.ToString() == key)
                    {
                        _deleteStarted.TrySetResult();
                        await _deleteRelease.Task;
                    }

                    lock (_deletedKeys)
                        _deletedKeys.Add(key);
                });
            Uploader = uploader.Object;
        }

        public S3BucketRegistry Registry { get; }

        public S3Uploader Uploader { get; }

        public Guid? HoldDeleteOf { get; init; }

        public Task DeleteStarted => _deleteStarted.Task;

        public void ReleaseDelete() => _deleteRelease.TrySetResult();

        public IReadOnlyList<string> DeletedKeys
        {
            get
            {
                lock (_deletedKeys)
                    return _deletedKeys.ToList();
            }
        }
    }

    /// <summary>
    /// Точка остановки purge: срабатывает один раз — перед коммитом, когда все проверки уже сделаны, но
    /// результат ещё не виден другим. Либо ждёт вторую сторону (<see cref="CountdownEvent"/>), либо сообщает
    /// о достижении точки и ждёт разрешения.
    /// </summary>
    private sealed class StopPoint
    {
        private readonly Action _stop;
        private int _fired;

        private StopPoint(Action stop) => _stop = stop;

        public static StopPoint WaitFor(CountdownEvent gate) => new(() =>
        {
            gate.Signal();
            gate.Wait(BarrierTimeout);
        });

        public static StopPoint Hold(ManualResetEventSlim reached, ManualResetEventSlim proceed) => new(() =>
        {
            reached.Set();
            proceed.Wait(Limit);
        });

        public void Once()
        {
            if (Interlocked.Exchange(ref _fired, 1) == 0)
                _stop();
        }
    }

    private sealed class CommitBarrier(StopPoint stop) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            stop.Once();
            return ValueTask.FromResult(result);
        }
    }
}
