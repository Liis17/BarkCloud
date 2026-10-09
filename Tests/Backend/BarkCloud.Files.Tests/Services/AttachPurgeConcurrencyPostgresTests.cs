using System.Collections.Concurrent;
using System.Data.Common;

using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.AttachFile;
using BarkCloud.Files.Features.Cloud.DeleteUserMedia;
using BarkCloud.Files.Features.Cloud.RestoreFromTrash;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using FileNotFoundException = BarkCloud.Shared.Exceptions.Files.FileNotFoundException;

namespace BarkCloud.Files.Tests.Services;

public sealed class AttachPurgeConcurrencyPostgresTests
{
    private const long Owner = 42;
    private const string Profile = "test-storage";
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(20);

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachFirst_PurgeKeepsLiveReferenceAndOriginal(bool expired)
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var file = Blob(Owner);
        var trash = Entry(file.Id);
        await Seed(database, file, trash);
        var s3 = new FakeS3(file.Id);

        var result = await Race(database, new EntrySaveBarrier(),
            (context, ct) => Attach(context, file.Id, ct),
            (context, ct) => Purge(context, s3, [trash], expired, ct));

        result.First.Should().BeNull();
        result.Second.Should().BeNull();
        await using var verify = database.CreateContext();
        (await verify.CloudFileEntries.CountAsync(e => e.FileId == file.Id && !e.IsDeleted)).Should().Be(1);
        var original = await verify.UploadedFiles.SingleOrDefaultAsync(f => f.Id == file.Id);
        original.Should().NotBeNull("успешная живая ссылка должна иметь оригинал");
        original!.Uploaders.Should().Contain(Owner);
        s3.Objects.Should().ContainKey(file.Id);
        s3.Deleted.Should().BeEmpty();
        result.Waited.Should().BeTrue("purge должен ждать общей границы Attach");

        var retry = () => Attach(verify, file.Id, default);
        await retry.Should().ThrowAsync<FileAlreadyAttachedException>();
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PurgeFirst_AttachReadsReleasedOwnershipAndRejects(bool expired)
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var file = Blob(Owner);
        var trash = Entry(file.Id);
        await Seed(database, file, trash);
        var s3 = new FakeS3(file.Id);

        var result = await Race(database, new CommitBarrier(),
            (context, ct) => Purge(context, s3, [trash], expired, ct),
            (context, ct) => Attach(context, file.Id, ct));

        result.First.Should().BeNull();
        AssertRejectedAttach(result.Second);
        result.Waited.Should().BeTrue();
        await using var verify = database.CreateContext();
        (await verify.CloudFileEntries.CountAsync()).Should().Be(0);
        (await verify.UploadedFiles.AnyAsync(f => f.Id == file.Id)).Should().BeFalse();
        s3.Objects.Should().NotContainKey(file.Id);
        s3.Deleted.Should().ContainSingle().Which.Should().Be(file.Id.ToString());
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreAndPurge_SerializeBothOrders(bool purgeFirst)
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var file = Blob(Owner);
        var trash = Entry(file.Id);
        await Seed(database, file, trash);
        var s3 = new FakeS3(file.Id);
        Func<FilesContext, CancellationToken, Task> restore = (context, ct) =>
            new RestoreFromTrashCommandHandler(new CloudHierarchyStorage(context), UserContextFactory.Create(Owner),
                NullLogger<RestoreFromTrashCommandHandler>.Instance)
                .Handle(new RestoreFromTrashCommand { EntryId = trash.Id }, ct);
        Func<FilesContext, CancellationToken, Task> purge = (context, ct) => Purge(context, s3, [trash], true, ct);

        var result = await Race(database, purgeFirst ? new CommitBarrier() : new EntrySaveBarrier(),
            purgeFirst ? purge : restore, purgeFirst ? restore : purge);

        result.First.Should().BeNull();
        result.Waited.Should().BeTrue();
        await using var verify = database.CreateContext();
        if (purgeFirst)
        {
            result.Second.Should().BeOfType<FileEntryNotFoundException>();
            (await verify.CloudFileEntries.CountAsync()).Should().Be(0);
            s3.Objects.Should().BeEmpty();
        }
        else
        {
            result.Second.Should().BeNull();
            (await verify.CloudFileEntries.SingleAsync()).IsDeleted.Should().BeFalse();
            s3.Objects.Should().ContainKey(file.Id);
            s3.Deleted.Should().BeEmpty();
        }
    }

    [PostgresFact]
    public async Task DeleteUserMedia_CreatesTrashBeforeAttachAndPurge()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var file = Blob(Owner);
        await Seed(database, file);
        var s3 = new FakeS3(file.Id);

        var result = await Race(database, new EntrySaveBarrier(),
            (context, ct) => DeleteMedia(context, file.Id, ct),
            (context, ct) => Attach(context, file.Id, ct));

        result.First.Should().BeNull();
        result.Second.Should().BeNull();
        result.Waited.Should().BeTrue();
        await using var verify = database.CreateContext();
        var trash = await verify.CloudFileEntries.AsNoTracking().Where(e => e.IsDeleted).ToListAsync();
        trash.Should().ContainSingle();
        await CreatePurge(verify, s3).PurgeEntriesAsync(trash, default);
        (await verify.CloudFileEntries.CountAsync(e => !e.IsDeleted)).Should().Be(1);
        s3.Objects.Should().ContainKey(file.Id);
    }

    [PostgresFact]
    public async Task PurgeFirst_DeleteUserMediaDoesNotCreateDanglingTrash()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var file = Blob(Owner);
        var trash = Entry(file.Id);
        await Seed(database, file, trash);
        var s3 = new FakeS3(file.Id);

        var result = await Race(database, new CommitBarrier(),
            (context, ct) => Purge(context, s3, [trash], false, ct),
            (context, ct) => DeleteMedia(context, file.Id, ct));

        result.First.Should().BeNull();
        AssertRejectedAttach(result.Second);
        result.Waited.Should().BeTrue();
        await using var verify = database.CreateContext();
        (await verify.CloudFileEntries.CountAsync()).Should().Be(0);
        (await verify.CloudDirectories.CountAsync()).Should().Be(0);
    }

    [PostgresFact]
    public async Task SeveralOwnersAndReferences_ReleaseOnlyUnreferencedPair()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var file = Blob(Owner, Owner + 1);
        var trash1 = Entry(file.Id);
        var trash2 = Entry(file.Id);
        var otherOwner = Entry(file.Id, Owner + 1, deleted: false);
        var favorite = new FavoriteFile { Id = Guid.NewGuid(), OwnerId = Owner + 1, FileId = file.Id, CreatedAt = DateTime.UtcNow };
        await Seed(database, file, trash1, trash2, otherOwner, favorite);
        var s3 = new FakeS3(file.Id);
        await using var context = database.CreateContext();
        var purge = CreatePurge(context, s3);

        (await purge.PurgeEntriesAsync([trash1], default)).Entries.Should().Be(1);
        (await context.UploadedFiles.AsNoTracking().SingleAsync()).Uploaders.Should().BeEquivalentTo([Owner, Owner + 1]);
        await purge.PurgeEntriesAsync([trash2], default);
        (await context.UploadedFiles.AsNoTracking().SingleAsync()).Uploaders.Should().BeEquivalentTo([Owner + 1]);
        (await context.FavoriteFiles.CountAsync()).Should().Be(1);
        s3.Deleted.Should().BeEmpty();
        AssertRejectedAttach(await Capture(() => Attach(context, file.Id, default)));
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentBatches_OppositeInputOrder_PurgeSharedBlobsAndPreview(bool sameOwners)
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var first = Blob(Owner, Owner + 1);
        var second = Blob(Owner, Owner + 1);
        var preview = Blob(Owner, Owner + 1);
        var entries = new[] { Entry(first.Id), Entry(second.Id), Entry(first.Id, Owner + 1), Entry(second.Id, Owner + 1) };
        await Seed(database, first, second, preview, Preview(first.Id, preview.Id), Preview(second.Id, preview.Id));
        await Seed(database, entries.Cast<object>().ToArray());
        var s3 = new FakeS3(first.Id, second.Id, preview.Id);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstBatch = sameOwners ? new[] { entries[0], entries[3] } : entries[..2];
        var secondBatch = sameOwners ? new[] { entries[2], entries[1] } : entries[2..].Reverse().ToArray();
        async Task Run(CloudFileEntry[] batch)
        {
            await using var context = database.CreateContext();
            await start.Task;
            await CreatePurge(context, s3).PurgeEntriesAsync(batch, default);
        }
        var tasks = new[] { Run(firstBatch), Run(secondBatch) };
        start.SetResult();
        await Task.WhenAll(tasks).WaitAsync(Safety);

        await using var verify = database.CreateContext();
        (await verify.UploadedFiles.CountAsync()).Should().Be(0);
        (await verify.CloudFileEntries.CountAsync()).Should().Be(0);
        s3.Deleted.Should().HaveCount(3).And.OnlyHaveUniqueItems();
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelWaitingOperation_LeavesDataAndReleasesLocks(bool cancelPurge)
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var file = Blob(Owner);
        var trash = Entry(file.Id);
        await Seed(database, file, trash);
        var s3 = new FakeS3(file.Id);
        using var timeout = new CancellationTokenSource(Safety);
        await using (var holder = database.CreateContext())
        await using (var treeLock = await new CloudHierarchyStorage(holder).LockTree(Owner, timeout.Token))
        {
            var attempt = new TreeLockAttempt();
            await using var waiting = database.CreateContext(attempt);
            var task = Capture(() => cancelPurge
                ? Purge(waiting, s3, [trash], false, timeout.Token)
                : Attach(waiting, file.Id, timeout.Token));
            try
            {
                (await WaitForLockOrCompletion(database, task, attempt, timeout.Token)).Should().BeTrue();
            }
            finally
            {
                timeout.Cancel();
                await task.WaitAsync(Safety);
            }
            (await task).Should().BeAssignableTo<OperationCanceledException>();
        }

        await using var verify = database.CreateContext();
        (await verify.CloudFileEntries.SingleAsync()).IsDeleted.Should().BeTrue();
        (await verify.UploadedFiles.SingleAsync()).Uploaders.Should().Contain(Owner);
        s3.Deleted.Should().BeEmpty();
        await Attach(verify, file.Id, default).WaitAsync(Safety);
    }

    [PostgresFact]
    public async Task S3Failure_RollsBackClaimAndRetriesAfterTreeLockWasReleased()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var file = Blob(Owner);
        var preview = Blob(Owner);
        var trash = Entry(file.Id);
        await Seed(database, file, preview, trash, Preview(file.Id, preview.Id),
            new FileHash { FileId = file.Id, Hash = new string('a', 64) });
        var s3 = new FakeS3(file.Id, preview.Id) { FailDeletes = true, HoldDelete = file.Id };
        await using var context = database.CreateContext();
        var purge = CreatePurge(context, s3).PurgeEntriesAsync([trash], default);
        await s3.DeleteStarted.Task.WaitAsync(Safety);
        try
        {
            using var timeout = new CancellationTokenSource(Safety);
            await using var attachContext = database.CreateContext();
            AssertRejectedAttach(await Capture(() => Attach(attachContext, file.Id, timeout.Token)).WaitAsync(Safety));
        }
        finally
        {
            s3.ReleaseDelete.TrySetResult();
            await purge.WaitAsync(Safety);
        }
        (await purge).Blobs.Should().Be(0);
        await using var verify = database.CreateContext();
        (await verify.UploadedFiles.CountAsync()).Should().Be(2);
        (await verify.UploadedFiles.AsNoTracking().SingleAsync(f => f.Id == file.Id)).Uploaders.Should().BeEmpty();
        (await verify.FileHashes.CountAsync()).Should().Be(1);
        (await verify.FilePreviews.CountAsync()).Should().Be(1);
        s3.Objects.Should().HaveCount(2);

        s3.FailDeletes = false;
        (await CreatePurge(verify, s3).PurgeOrphanBlobsAsync([file.Id], default)).Should().Be(2);
        (await verify.UploadedFiles.CountAsync()).Should().Be(0);
        (await verify.FileHashes.CountAsync()).Should().Be(0);
        (await verify.FilePreviews.CountAsync()).Should().Be(0);
        s3.Objects.Should().BeEmpty();
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelBeforeCommit_RollsBackAttachOrPurge(bool cancelPurge)
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var file = Blob(Owner);
        var trash = Entry(file.Id);
        var favorite = new FavoriteFile { Id = Guid.NewGuid(), OwnerId = Owner, FileId = file.Id, CreatedAt = DateTime.UtcNow };
        await Seed(database, file, trash, favorite);
        var s3 = new FakeS3(file.Id);
        Barrier barrier = cancelPurge ? new CommitBarrier() : new EntrySaveBarrier();
        using var timeout = new CancellationTokenSource(Safety);
        await using (var context = database.CreateContext(barrier.Interceptor))
        {
            var task = Capture(() => cancelPurge
                ? Purge(context, s3, [trash], false, timeout.Token)
                : Attach(context, file.Id, timeout.Token));
            try
            {
                (await Task.WhenAny(barrier.Reached, task).WaitAsync(Safety)).Should().Be(barrier.Reached);
                timeout.Cancel();
                (await task.WaitAsync(Safety)).Should().BeAssignableTo<OperationCanceledException>();
            }
            finally
            {
                timeout.Cancel();
                barrier.Release();
                await task.WaitAsync(Safety);
            }
        }
        await using var verify = database.CreateContext();
        (await verify.CloudFileEntries.SingleAsync()).Id.Should().Be(trash.Id);
        (await verify.UploadedFiles.SingleAsync()).Uploaders.Should().Contain(Owner);
        (await verify.FavoriteFiles.CountAsync()).Should().Be(1);
        s3.Deleted.Should().BeEmpty();
        await Attach(verify, file.Id, default).WaitAsync(Safety);
    }

    [PostgresFact]
    public async Task AddOwnerWhileOrphanClaimIsDeleting_DoesNotResurrectOriginal()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var file = Blob();
        await Seed(database, file);
        var s3 = new FakeS3(file.Id) { HoldDelete = file.Id };
        await using var cleanupContext = database.CreateContext();
        var cleanup = CreatePurge(cleanupContext, s3).PurgeOrphanBlobsAsync([file.Id], default);
        await s3.DeleteStarted.Task.WaitAsync(Safety);
        using var timeout = new CancellationTokenSource(Safety);
        await using var adding = database.CreateContext();
        await adding.Database.OpenConnectionAsync(timeout.Token);
        var pid = ((NpgsqlConnection)adding.Database.GetDbConnection()).ProcessID;
        var add = new UploadedFilesStorage(adding).AddUploaderToFile(file.Id, Owner);
        try
        {
            await using var connection = await database.DataSource.OpenConnectionAsync(timeout.Token);
            await using var command = new NpgsqlCommand("SELECT cardinality(pg_blocking_pids($1)) > 0", connection);
            command.Parameters.AddWithValue(pid);
            var waited = false;
            while (!add.IsCompleted)
            {
                if (await command.ExecuteScalarAsync(timeout.Token) is true)
                {
                    waited = true;
                    break;
                }
                await Task.Delay(10, timeout.Token);
            }
            waited.Should().BeTrue("добавление владельца должно ждать захваченную строку оригинала");
        }
        finally
        {
            s3.ReleaseDelete.TrySetResult();
            await Task.WhenAll(cleanup, add).WaitAsync(Safety);
        }
        (await cleanup).Should().Be(1);
        (await adding.UploadedFiles.CountAsync()).Should().Be(0);
        (await adding.CloudFileEntries.CountAsync()).Should().Be(0);
        AssertRejectedAttach(await Capture(() => Attach(adding, file.Id, default)));
    }

    [PostgresFact]
    public async Task OrphanCleanup_ReferencedBlobWithEmptyUploaders_IsNotClaimed()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var file = Blob();
        await Seed(database, file, Entry(file.Id));
        var s3 = new FakeS3(file.Id);
        await using var context = database.CreateContext();

        (await CreatePurge(context, s3).PurgeOrphanBlobsAsync([file.Id], default)).Should().Be(0);
        (await context.UploadedFiles.CountAsync()).Should().Be(1);
        s3.Deleted.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task AddOwnerBeforeOrphanClaim_KeepsOriginalAndAllowsAttach()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var file = Blob();
        await Seed(database, file);
        var s3 = new FakeS3(file.Id);
        await using var context = database.CreateContext();
        await new UploadedFilesStorage(context).AddUploaderToFile(file.Id, Owner);

        (await CreatePurge(context, s3).PurgeOrphanBlobsAsync([file.Id], default)).Should().Be(0);
        await Attach(context, file.Id, default);
        s3.Deleted.Should().BeEmpty();
    }

    private static Task Attach(FilesContext context, Guid fileId, CancellationToken ct) =>
        new AttachFileCommandHandler(new CloudHierarchyStorage(context), new UploadedFilesStorage(context),
            UserContextFactory.Create(Owner), NullLogger<AttachFileCommandHandler>.Instance)
            .Handle(new AttachFileCommand { FileId = fileId, Name = "attached.txt" }, ct);

    private static Task DeleteMedia(FilesContext context, Guid fileId, CancellationToken ct) =>
        new DeleteUserMediaCommandHandler(new CloudHierarchyStorage(context), new UploadedFilesStorage(context),
            UserContextFactory.Create(Owner), NullLogger<DeleteUserMediaCommandHandler>.Instance)
            .Handle(new DeleteUserMediaCommand { FileId = fileId }, ct);

    private static Task Purge(FilesContext context, FakeS3 s3, CloudFileEntry[] entries, bool expired, CancellationToken ct) =>
        expired ? CreatePurge(context, s3).PurgeExpiredEntriesAsync(entries, DateTime.UtcNow, ct)
            : CreatePurge(context, s3).PurgeEntriesAsync(entries, ct);

    private static TrashPurgeService CreatePurge(FilesContext context, FakeS3 s3) =>
        new(context, s3.Uploader, s3.Registry, new FileHashesStorage(context), NullLogger<TrashPurgeService>.Instance);

    private static void AssertRejectedAttach(Exception? error) =>
        (error is FileNotFoundException or CloudAccessDeniedException).Should().BeTrue($"ожидается доменный отказ, получено {error}");

    private static UploadFile Blob(params long[] owners) => new()
    {
        Id = Guid.NewGuid(), Uploaders = owners.ToList(), StorageProfileId = Profile,
        Type = UploadFileType.CloudFile, MediaKind = MediaKind.Document, Filename = "original.txt",
        CreatedAt = DateTime.UtcNow, UploadedAt = DateTime.UtcNow, Etag = "etag",
    };

    private static CloudFileEntry Entry(Guid fileId, long owner = Owner, bool deleted = true) => new()
    {
        Id = Guid.NewGuid(), OwnerId = owner, FileId = fileId, DirectoryId = Guid.Empty,
        Name = "original.txt", CreatedAt = DateTime.UtcNow, IsDeleted = deleted,
        DeletedAt = deleted ? DateTime.UtcNow.AddDays(-15) : null,
        PurgeAt = deleted ? DateTime.UtcNow.AddDays(-1) : null,
    };

    private static FilePreview Preview(Guid original, Guid preview) => new()
    {
        Id = Guid.NewGuid(), OriginalFileId = original, PreviewFileId = preview, TargetWidth = 128, CreatedAt = DateTime.UtcNow,
    };

    private static async Task Seed(PostgresFilesDatabase database, params object[] entities)
    {
        await using var context = database.CreateContext();
        context.AddRange(entities);
        await context.SaveChangesAsync();
    }

    private static async Task<(Exception? First, Exception? Second, bool Waited)> Race(
        PostgresFilesDatabase database, Barrier barrier,
        Func<FilesContext, CancellationToken, Task> first, Func<FilesContext, CancellationToken, Task> second)
    {
        using var timeout = new CancellationTokenSource(Safety);
        var attempt = new TreeLockAttempt();
        await using var firstContext = database.CreateContext(barrier.Interceptor);
        await using var secondContext = database.CreateContext(attempt);
        var firstTask = Capture(() => first(firstContext, timeout.Token));
        Task<Exception?>? secondTask = null;
        var waited = false;
        try
        {
            (await Task.WhenAny(barrier.Reached, firstTask).WaitAsync(timeout.Token)).Should().Be(barrier.Reached);
            secondTask = Capture(() => second(secondContext, timeout.Token));
            waited = await WaitForLockOrCompletion(database, secondTask, attempt, timeout.Token);
        }
        finally
        {
            barrier.Release();
            await firstTask.WaitAsync(Safety);
            if (secondTask is not null)
                await secondTask.WaitAsync(Safety);
        }
        return (await firstTask, await secondTask!, waited);
    }

    private static async Task<bool> WaitForLockOrCompletion(
        PostgresFilesDatabase database, Task task, TreeLockAttempt attempt, CancellationToken ct)
    {
        await Task.WhenAny(task, attempt.Started).WaitAsync(ct);
        if (task.IsCompleted)
            return false;
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE pid = $1 AND locktype = 'advisory' AND NOT granted)", connection);
        command.Parameters.AddWithValue(await attempt.Started);
        while (!task.IsCompleted)
        {
            if (await command.ExecuteScalarAsync(ct) is true)
                return true;
            await Task.Delay(10, ct);
        }
        return false;
    }

    private static async Task<Exception?> Capture(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return error; }
    }

    private sealed class TreeLockAttempt : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<int> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<int> Started => _started.Task;
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal))
                _started.TrySetResult(((NpgsqlConnection)command.Connection!).ProcessID);
            return ValueTask.FromResult(result);
        }
    }

    private abstract class Barrier
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _used;
        public Task Reached => _reached.Task;
        public abstract IInterceptor Interceptor { get; }
        public void Release() => _release.TrySetResult();
        protected async Task Pause(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _used, 1) != 0)
                return;
            _reached.TrySetResult();
            await _release.Task.WaitAsync(Safety, ct);
        }

        protected sealed class Save(EntrySaveBarrier barrier) : SaveChangesInterceptor
        {
            public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
                DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                if (eventData.Context!.ChangeTracker.Entries<CloudFileEntry>().Any(e => e.State is EntityState.Added or EntityState.Modified))
                    await barrier.Pause(cancellationToken);
                return result;
            }
        }

        protected sealed class Commit(CommitBarrier barrier) : DbTransactionInterceptor
        {
            public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
                DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
            {
                await barrier.Pause(cancellationToken);
                return result;
            }
        }
    }

    private sealed class EntrySaveBarrier : Barrier
    {
        public override IInterceptor Interceptor => new Save(this);
    }

    private sealed class CommitBarrier : Barrier
    {
        public override IInterceptor Interceptor => new Commit(this);
    }

    private sealed class FakeS3
    {
        public S3Uploader Uploader { get; }
        public S3BucketRegistry Registry { get; }
        public ConcurrentDictionary<Guid, bool> Objects { get; } = new();
        public ConcurrentBag<string> Deleted { get; } = new();
        public bool FailDeletes { get; set; }
        public Guid? HoldDelete { get; init; }
        public TaskCompletionSource DeleteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDelete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FakeS3(params Guid[] objects)
        {
            foreach (var id in objects)
                Objects[id] = true;
            var registry = new Mock<S3BucketRegistry>(TestConfiguration.Empty());
            registry.Setup(r => r.ResolveReadProfileId(It.IsAny<UploadFile>())).Returns(Profile);
            Registry = registry.Object;
            var s3 = new Mock<S3Uploader>(Registry);
            s3.Setup(s => s.DeleteAsync(Profile, It.IsAny<string>())).Returns<string, string>(async (profile, key) =>
            {
                var id = Guid.Parse(key);
                if (HoldDelete == id)
                {
                    DeleteStarted.TrySetResult();
                    await ReleaseDelete.Task.WaitAsync(Safety);
                }
                if (FailDeletes)
                    throw new IOException("S3 unavailable");
                Objects.TryRemove(id, out _);
                Deleted.Add(key);
            });
            Uploader = s3.Object;
        }
    }
}
