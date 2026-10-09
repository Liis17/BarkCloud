using System.Data.Common;

using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.DeleteDirectory;
using BarkCloud.Files.Features.Cloud.DeleteFileEntry;
using BarkCloud.Files.Features.Cloud.MoveFileEntry;
using BarkCloud.Files.Features.Cloud.RenameFileEntry;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

namespace BarkCloud.Files.Tests.Persistence;

public sealed class CloudFileEntryConcurrencyPostgresTests
{
    private const long OwnerId = 42;
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(20);

    public enum FileMutation { Rename, Move, Delete }

    [PostgresFact]
    public async Task RenameFirst_DeleteDirectory_DoesNotResurrectEntryOrEraseTrashDates()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var directory = Directory("Source");
        var entry = Entry(directory.Id);
        await Seed(database, directory, entry);
        CloudFileEntry? deleted = null;

        var outcomes = await RunRace(database,
            (context, ct) => Rename(context, entry.Id, ct),
            async (context, ct) =>
            {
                await DeleteDirectory(context, directory.Id, ct);
                deleted = await ReadEntry(database, entry.Id);
            });

        outcomes.First.Should().BeNull();
        outcomes.Second.Should().BeNull();
        await using var verify = database.CreateContext();
        (await verify.CloudDirectories.AnyAsync(d => d.Id == directory.Id)).Should().BeFalse();
        var actual = await new CloudHierarchyStorage(verify).GetFileEntry(entry.Id);
        actual!.IsDeleted.Should().BeTrue("rename не должен возвращать файл из корзины");
        actual.DeletedAt.Should().NotBeNull().And.Be(deleted!.DeletedAt);
        actual.PurgeAt.Should().NotBeNull().And.Be(deleted.PurgeAt);
        outcomes.SecondWaited.Should().BeTrue();
    }

    [PostgresFact]
    public async Task DeleteDirectoryFirst_Rename_RejectsTrashedEntry()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var directory = Directory("Source");
        var entry = Entry(directory.Id);
        await Seed(database, directory, entry);
        CloudFileEntry? deleted = null;

        var outcomes = await RunRace(database,
            async (context, ct) =>
            {
                await DeleteDirectory(context, directory.Id, ct);
                deleted = await ReadEntry(database, entry.Id);
            },
            (context, ct) => Rename(context, entry.Id, ct));

        outcomes.First.Should().BeNull();
        outcomes.Second.Should().BeOfType<FileEntryNotFoundException>();
        outcomes.SecondWaited.Should().BeTrue();
        var actual = (await ReadEntry(database, entry.Id))!;
        actual.Name.Should().Be("original.txt");
        actual.IsDeleted.Should().BeTrue();
        actual.DeletedAt.Should().Be(deleted!.DeletedAt);
        actual.PurgeAt.Should().Be(deleted.PurgeAt);
    }

    [PostgresFact]
    public async Task RenameFirst_DeleteFileEntry_PreservesNameAndTrashDates()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var entry = Entry(CloudHierarchyStorage.RootDirectoryId);
        await Seed(database, entry);
        CloudFileEntry? deleted = null;

        var outcomes = await RunRace(database,
            (context, ct) => Rename(context, entry.Id, ct),
            async (context, ct) =>
            {
                await DeleteFileEntry(context, entry.Id, ct);
                deleted = await ReadEntry(database, entry.Id);
            });

        outcomes.First.Should().BeNull();
        outcomes.Second.Should().BeNull();
        outcomes.SecondWaited.Should().BeTrue("одиночное удаление должно дождаться commit rename");
        var actual = (await ReadEntry(database, entry.Id))!;
        actual.Name.Should().Be("renamed.txt");
        actual.IsDeleted.Should().BeTrue();
        actual.DeletedAt.Should().NotBeNull().And.Be(deleted!.DeletedAt);
        actual.PurgeAt.Should().NotBeNull().And.Be(deleted.PurgeAt);
    }

    [PostgresFact]
    public async Task DeleteFileEntryFirst_Rename_RejectsTrashedEntry()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var entry = Entry(CloudHierarchyStorage.RootDirectoryId);
        await Seed(database, entry);
        CloudFileEntry? deleted = null;

        var outcomes = await RunRace(database,
            async (context, ct) =>
            {
                await DeleteFileEntry(context, entry.Id, ct);
                deleted = await ReadEntry(database, entry.Id);
            },
            (context, ct) => Rename(context, entry.Id, ct));

        outcomes.First.Should().BeNull();
        outcomes.Second.Should().BeOfType<FileEntryNotFoundException>();
        outcomes.SecondWaited.Should().BeTrue();
        var actual = (await ReadEntry(database, entry.Id))!;
        actual.Name.Should().Be("original.txt");
        actual.IsDeleted.Should().BeTrue();
        actual.DeletedAt.Should().Be(deleted!.DeletedAt);
        actual.PurgeAt.Should().Be(deleted.PurgeAt);
    }

    [PostgresFact]
    public async Task UpdateFileEntry_Rename_PreservesConcurrentLocationAndTrashDates()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var source = Directory("Source");
        var target = Directory("Target");
        var entry = Entry(source.Id);
        await Seed(database, source, target, entry);
        await using var staleContext = database.CreateContext();
        var storage = new CloudHierarchyStorage(staleContext);
        var stale = (await storage.GetFileEntry(entry.Id))!;
        var deletedAt = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        var purgeAt = deletedAt.AddDays(14);
        await using (var concurrent = database.CreateContext())
        {
            await concurrent.CloudFileEntries.Where(e => e.Id == entry.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(e => e.DirectoryId, target.Id)
                .SetProperty(e => e.IsDeleted, true)
                .SetProperty(e => e.DeletedAt, deletedAt)
                .SetProperty(e => e.PurgeAt, purgeAt));
        }

        stale.Name = "renamed.txt";
        await storage.UpdateFileEntry(stale);

        var actual = (await ReadEntry(database, entry.Id))!;
        actual.Name.Should().Be("renamed.txt");
        actual.DirectoryId.Should().Be(target.Id);
        actual.IsDeleted.Should().BeTrue();
        actual.DeletedAt.Should().Be(deletedAt);
        actual.PurgeAt.Should().Be(purgeAt);
    }

    [PostgresFact]
    public async Task UpdateFileEntry_Move_PreservesConcurrentNameAndTrashDates()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var source = Directory("Source");
        var target = Directory("Target");
        var entry = Entry(source.Id);
        await Seed(database, source, target, entry);
        await using var staleContext = database.CreateContext();
        var storage = new CloudHierarchyStorage(staleContext);
        var stale = (await storage.GetFileEntry(entry.Id))!;
        var deletedAt = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        var purgeAt = deletedAt.AddDays(14);
        await using (var concurrent = database.CreateContext())
        {
            await concurrent.CloudFileEntries.Where(e => e.Id == entry.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(e => e.Name, "concurrent.txt")
                .SetProperty(e => e.IsDeleted, true)
                .SetProperty(e => e.DeletedAt, deletedAt)
                .SetProperty(e => e.PurgeAt, purgeAt));
        }

        stale.DirectoryId = target.Id;
        await storage.UpdateFileEntry(stale);

        var actual = (await ReadEntry(database, entry.Id))!;
        actual.DirectoryId.Should().Be(target.Id);
        actual.Name.Should().Be("concurrent.txt");
        actual.IsDeleted.Should().BeTrue();
        actual.DeletedAt.Should().Be(deletedAt);
        actual.PurgeAt.Should().Be(purgeAt);
    }

    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RenameAndMove_BothOrders_PreserveNameAndNewLocation(bool renameFirst)
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var source = Directory("Source");
        var target = Directory("Target");
        var entry = Entry(source.Id);
        await Seed(database, source, target, entry);

        var outcomes = await RunRace(database,
            (context, ct) => renameFirst ? Rename(context, entry.Id, ct) : Move(context, entry.Id, target.Id, ct),
            (context, ct) => renameFirst ? Move(context, entry.Id, target.Id, ct) : Rename(context, entry.Id, ct));

        outcomes.First.Should().BeNull();
        outcomes.Second.Should().BeNull();
        outcomes.SecondWaited.Should().BeTrue();
        var actual = (await ReadEntry(database, entry.Id))!;
        actual.Name.Should().Be("renamed.txt");
        actual.DirectoryId.Should().Be(target.Id);
        actual.IsDeleted.Should().BeFalse();
        await using var verify = database.CreateContext();
        var kinds = await verify.FileActivityEvents.Select(e => e.Kind).ToListAsync();
        kinds.Should().BeEquivalentTo([FileActivityKind.Renamed, FileActivityKind.Moved]);
    }

    [PostgresFact]
    public async Task MoveFirst_Rename_ChecksNameConflictInNewDirectory()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var source = Directory("Source");
        var target = Directory("Target");
        var entry = Entry(source.Id);
        await Seed(database, source, target, entry, Entry(target.Id, "renamed.txt"));

        var outcomes = await RunRace(database,
            (context, ct) => Move(context, entry.Id, target.Id, ct),
            (context, ct) => Rename(context, entry.Id, ct));

        outcomes.First.Should().BeNull();
        outcomes.Second.Should().BeOfType<DirectoryNameConflictException>();
        outcomes.SecondWaited.Should().BeTrue();
        var actual = (await ReadEntry(database, entry.Id))!;
        actual.Name.Should().Be("original.txt");
        actual.DirectoryId.Should().Be(target.Id);
        await using var verify = database.CreateContext();
        (await verify.FileActivityEvents.Select(e => e.Kind).ToListAsync()).Should().Equal(FileActivityKind.Moved);
    }

    [PostgresTheory]
    [InlineData(FileMutation.Rename)]
    [InlineData(FileMutation.Move)]
    [InlineData(FileMutation.Delete)]
    public async Task EntryPhysicallyDisappearsBeforeSave_ReturnsNotFoundWithoutActivity(FileMutation mutation)
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var source = Directory("Source");
        var target = Directory("Target");
        var entry = Entry(source.Id);
        await Seed(database, source, target, entry);

        var outcomes = await RunRace(database,
            (context, ct) => Mutate(context, mutation, entry.Id, target.Id, ct),
            (context, ct) => context.CloudFileEntries.Where(e => e.Id == entry.Id).ExecuteDeleteAsync(ct));

        outcomes.First.Should().BeOfType<FileEntryNotFoundException>();
        outcomes.Second.Should().BeNull();
        (await ReadEntry(database, entry.Id)).Should().BeNull();
        await using var verify = database.CreateContext();
        (await verify.FileActivityEvents.CountAsync()).Should().Be(0);
        await AssertTreeLockReleased(database);
    }

    [PostgresTheory]
    [InlineData(FileMutation.Rename)]
    [InlineData(FileMutation.Move)]
    [InlineData(FileMutation.Delete)]
    public async Task CancelWhileWaitingForTreeLock_DoesNotMutateOrWriteActivity(FileMutation mutation)
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var source = Directory("Source");
        var target = Directory("Target");
        var entry = Entry(source.Id);
        await Seed(database, source, target, entry);
        using var cancellation = new CancellationTokenSource(Safety);
        await using (var holder = database.CreateContext())
        {
            await using var treeLock = await new CloudHierarchyStorage(holder).LockTree(OwnerId, cancellation.Token);
            var attempt = new TreeLockAttempt();
            await using var context = database.CreateContext(attempt);
            var task = Capture(() => Mutate(context, mutation, entry.Id, target.Id, cancellation.Token));
            try
            {
                (await WaitForCompletionOrTreeLockWait(database, task, attempt, cancellation.Token)).Should().BeTrue();
            }
            finally
            {
                cancellation.Cancel();
                await task.WaitAsync(Safety);
            }
            (await task).Should().BeAssignableTo<OperationCanceledException>();
        }

        await AssertOriginalEntryWithoutActivity(database, entry);
        await AssertTreeLockReleased(database);
    }

    [PostgresTheory]
    [InlineData(FileMutation.Rename)]
    [InlineData(FileMutation.Move)]
    [InlineData(FileMutation.Delete)]
    public async Task CancelBeforeSave_RollsBackAndReleasesTreeLock(FileMutation mutation)
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var source = Directory("Source");
        var target = Directory("Target");
        var entry = Entry(source.Id);
        await Seed(database, source, target, entry);
        using var cancellation = new CancellationTokenSource(Safety);
        var pause = new PauseBeforeEntrySave();
        await using (var context = database.CreateContext(pause))
        {
            var task = Capture(() => Mutate(context, mutation, entry.Id, target.Id, cancellation.Token));
            try
            {
                (await Task.WhenAny(pause.Reached, task).WaitAsync(Safety)).Should().Be(pause.Reached);
                cancellation.Cancel();
                (await task.WaitAsync(Safety)).Should().BeAssignableTo<OperationCanceledException>();
            }
            finally
            {
                cancellation.Cancel();
                pause.Release();
                await task.WaitAsync(Safety);
            }

            await AssertOriginalEntryWithoutActivity(database, entry);
            await AssertTreeLockReleased(database);
        }
    }

    [PostgresTheory]
    [InlineData(FileMutation.Rename, false)]
    [InlineData(FileMutation.Move, false)]
    [InlineData(FileMutation.Delete, false)]
    [InlineData(FileMutation.Rename, true)]
    [InlineData(FileMutation.Move, true)]
    [InlineData(FileMutation.Delete, true)]
    public async Task SaveOrCommitFailure_RollsBackWithoutActivity(FileMutation mutation, bool failCommit)
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var source = Directory("Source");
        var target = Directory("Target");
        var entry = Entry(source.Id);
        await Seed(database, source, target, entry);
        Exception failure = failCommit ? new InvalidOperationException("commit failed") : new DbUpdateException("save failed");
        IInterceptor interceptor = failCommit ? new FailCommit(failure) : new FailAfterSave(failure);
        await using (var context = database.CreateContext(interceptor))
        {
            var error = await Capture(() => Mutate(context, mutation, entry.Id, target.Id, default)).WaitAsync(Safety);
            error.Should().BeSameAs(failure, "ошибки сохранения и commit не должны маскироваться как NotFound");
            await AssertOriginalEntryWithoutActivity(database, entry);
            await AssertTreeLockReleased(database);
        }
    }

    private static async Task AssertOriginalEntryWithoutActivity(PostgresFilesDatabase database, CloudFileEntry entry)
    {
        var actual = (await ReadEntry(database, entry.Id))!;
        actual.Name.Should().Be(entry.Name);
        actual.DirectoryId.Should().Be(entry.DirectoryId);
        actual.IsDeleted.Should().BeFalse();
        actual.DeletedAt.Should().BeNull();
        actual.PurgeAt.Should().BeNull();
        await using var verify = database.CreateContext();
        (await verify.FileActivityEvents.CountAsync()).Should().Be(0);
    }

    private static Task Rename(FilesContext context, Guid entryId, CancellationToken ct) =>
        new RenameFileEntryCommandHandler(
            new CloudHierarchyStorage(context), UserContextFactory.Create(OwnerId),
            NullLogger<RenameFileEntryCommandHandler>.Instance, Activity(context))
            .Handle(new RenameFileEntryCommand { EntryId = entryId, NewName = "renamed.txt" }, ct);

    private static Task DeleteDirectory(FilesContext context, Guid directoryId, CancellationToken ct) =>
        new DeleteDirectoryCommandHandler(
            new CloudHierarchyStorage(context), new FolderShareStorage(context), new DirectoryGrantStorage(context),
            UserContextFactory.Create(OwnerId), NullLogger<DeleteDirectoryCommandHandler>.Instance)
            .Handle(new DeleteDirectoryCommand { DirectoryId = directoryId }, ct);

    private static Task DeleteFileEntry(FilesContext context, Guid entryId, CancellationToken ct) =>
        new DeleteFileEntryCommandHandler(
            new CloudHierarchyStorage(context), UserContextFactory.Create(OwnerId),
            NullLogger<DeleteFileEntryCommandHandler>.Instance, Activity(context))
            .Handle(new DeleteFileEntryCommand { EntryId = entryId }, ct);

    private static Task Move(FilesContext context, Guid entryId, Guid targetId, CancellationToken ct) =>
        new MoveFileEntryCommandHandler(
            new CloudHierarchyStorage(context), UserContextFactory.Create(OwnerId),
            NullLogger<MoveFileEntryCommandHandler>.Instance, Activity(context))
            .Handle(new MoveFileEntryCommand { EntryId = entryId, NewDirectoryId = targetId }, ct);

    private static FileActivityWriter Activity(FilesContext context) =>
        new(new FileActivityStorage(context), NullLogger<FileActivityWriter>.Instance);

    private static Task Mutate(FilesContext context, FileMutation mutation, Guid entryId, Guid targetId, CancellationToken ct) =>
        mutation switch
        {
            FileMutation.Rename => Rename(context, entryId, ct),
            FileMutation.Move => Move(context, entryId, targetId, ct),
            FileMutation.Delete => DeleteFileEntry(context, entryId, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

    private static async Task AssertTreeLockReleased(PostgresFilesDatabase database)
    {
        using var timeout = new CancellationTokenSource(Safety);
        await using var context = database.CreateContext();
        await using var treeLock = await new CloudHierarchyStorage(context).LockTree(OwnerId, timeout.Token);
    }

    private static async Task<(Exception? First, Exception? Second, bool SecondWaited)> RunRace(
        PostgresFilesDatabase database,
        Func<FilesContext, CancellationToken, Task> first,
        Func<FilesContext, CancellationToken, Task> second)
    {
        using var timeout = new CancellationTokenSource(Safety);
        var pause = new PauseBeforeEntrySave();
        var attempt = new TreeLockAttempt();
        await using var firstContext = database.CreateContext(pause);
        await using var secondContext = database.CreateContext(attempt);
        var firstTask = Capture(() => first(firstContext, timeout.Token));
        Task<Exception?>? secondTask = null;
        var secondWaited = false;
        try
        {
            var reached = await Task.WhenAny(pause.Reached, firstTask).WaitAsync(Safety);
            reached.Should().Be(pause.Reached, "первый запрос должен остановиться перед UPDATE");
            secondTask = Capture(() => second(secondContext, timeout.Token));
            secondWaited = await WaitForCompletionOrTreeLockWait(database, secondTask, attempt, timeout.Token);
        }
        finally
        {
            pause.Release();
            await firstTask.WaitAsync(Safety);
            if (secondTask is not null)
                await secondTask.WaitAsync(Safety);
        }

        return (await firstTask, await secondTask!, secondWaited);
    }

    // На старом коде конкурент завершается раньше rename. После фикса он действительно ждёт
    // advisory-замок. Отпускаем барьер по наблюдаемому состоянию БД, а не после задержки.
    private static async Task<bool> WaitForCompletionOrTreeLockWait(
        PostgresFilesDatabase database, Task task, TreeLockAttempt attempt, CancellationToken ct)
    {
        await Task.WhenAny(task, attempt.Started).WaitAsync(ct);
        if (task.IsCompleted)
            return false;

        var pid = await attempt.Started;
        await using var connection = await database.DataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE pid = $1 AND locktype = 'advisory' AND NOT granted)", connection);
        command.Parameters.AddWithValue(pid);
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
        try
        {
            await action();
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private static CloudDirectory Directory(string name) => new()
    {
        Id = Guid.NewGuid(), OwnerId = OwnerId, Name = name,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    private static CloudFileEntry Entry(Guid directoryId, string name = "original.txt") => new()
    {
        Id = Guid.NewGuid(), OwnerId = OwnerId, DirectoryId = directoryId,
        FileId = Guid.NewGuid(), Name = name, CreatedAt = DateTime.UtcNow,
    };

    private static async Task Seed(PostgresFilesDatabase database, params object[] entities)
    {
        await using var context = database.CreateContext();
        CloudFileEntryFixtures.AddOriginals(context, entities.OfType<CloudFileEntry>());
        context.AddRange(entities);
        await context.SaveChangesAsync();
    }

    private static async Task<CloudFileEntry?> ReadEntry(PostgresFilesDatabase database, Guid entryId)
    {
        await using var context = database.CreateContext();
        return await new CloudHierarchyStorage(context).GetFileEntry(entryId);
    }

    private sealed class FailAfterSave(Exception failure) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default) =>
            throw failure;
    }

    private sealed class FailCommit(Exception failure) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default) => throw failure;
    }

    private sealed class TreeLockAttempt : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<int> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<int> Started => _started.Task;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal))
                _started.TrySetResult(((NpgsqlConnection)command.Connection!).ProcessID);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class PauseBeforeEntrySave : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _used;
        public Task Reached => _reached.Task;
        public void Release() => _release.TrySetResult();

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<CloudFileEntry>().Any(e => e.State == EntityState.Modified) &&
                Interlocked.Exchange(ref _used, 1) == 0)
            {
                _reached.TrySetResult();
                await _release.Task.WaitAsync(Safety, cancellationToken);
            }
            return result;
        }
    }
}
