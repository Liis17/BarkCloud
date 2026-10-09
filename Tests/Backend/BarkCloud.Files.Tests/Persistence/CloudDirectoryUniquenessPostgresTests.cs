using System.Data.Common;

using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.MoveDirectory;
using BarkCloud.Files.Features.Cloud.RenameDirectory;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

namespace BarkCloud.Files.Tests.Persistence;

public sealed class CloudDirectoryUniquenessPostgresTests
{
    private const long OwnerId = 42;

    [PostgresFact]
    public async Task ConcurrentRootCreates_OneSucceedsAndOtherGetsNameConflict()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var barrier = new DirectoryWriteBarrier();

        var outcomes = await Task.WhenAll(
            Capture(() => Create(database, barrier, "Docs")),
            Capture(() => Create(database, barrier, "Docs")));

        outcomes.Count(x => x is null).Should().Be(1);
        outcomes.Single(x => x is not null).Should().BeOfType<DirectoryNameConflictException>();
        await using var context = database.CreateContext();
        var directories = await new CloudHierarchyStorage(context).ListSubdirectories(OwnerId, null);
        directories.Should().ContainSingle().Which.Name.Should().Be("Docs");
    }

    [PostgresFact]
    public async Task ConcurrentSystemCreates_WithDifferentNames_ReturnSameId()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var barrier = new DirectoryWriteBarrier();

        var ids = await Task.WhenAll(
            Ensure(database, barrier, "Фото"),
            Ensure(database, barrier, "Pictures"));

        ids.Distinct().Should().ContainSingle();
        await using var context = database.CreateContext();
        var directories = await new CloudHierarchyStorage(context).ListSubdirectories(OwnerId, null);
        directories.Should().ContainSingle().Which.SystemKind.Should().Be(CloudDirectorySystemKind.Photos);
    }

    [PostgresFact]
    public async Task NormalFolderCreatedAfterLookup_IsPromotedInsteadOfCreatingSuffix()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var ordinary = Directory("Фото");
        var interceptor = new AfterCanonicalLookup(async () => await Seed(database, ordinary));
        await using var context = database.CreateContext(interceptor);
        var storage = new CloudHierarchyStorage(context);

        var id = await storage.EnsureSystemDirectory(OwnerId, CloudDirectorySystemKind.Photos, "Фото");

        id.Should().Be(ordinary.Id);
        (await storage.ListSubdirectories(OwnerId, null)).Should().ContainSingle()
            .Which.SystemKind.Should().Be(CloudDirectorySystemKind.Photos);
    }

    [PostgresFact]
    public Task ConcurrentSystemCreates_WithSameName_ReturnSameIdAndAllowNextWrite() => AssertSystemRace("Фото", promote: false);

    [PostgresFact]
    public Task ConcurrentPromotionAndCreate_ReturnSameIdAndAllowNextWrite() => AssertSystemRace("Pictures", promote: true);

    private static async Task AssertSystemRace(string otherName, bool promote)
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        if (promote)
            await Seed(database, Directory("Фото"));
        var barrier = new DirectoryWriteBarrier(insertFirst: promote);

        var ids = await Task.WhenAll(
            Ensure(database, barrier, "Фото", transaction: true),
            Ensure(database, barrier, otherName, transaction: true));

        ids.Distinct().Should().ContainSingle();
        await using var context = database.CreateContext();
        var storage = new CloudHierarchyStorage(context);
        var directories = await storage.ListSubdirectories(OwnerId, null);
        directories.Count(x => x.SystemKind == CloudDirectorySystemKind.Photos).Should().Be(1);
        (await storage.ListFilesInDirectory(OwnerId, ids[0])).Should().HaveCount(2);
        if (promote)
            directories.Single(x => x.Name == "Фото").SystemKind.Should().Be(CloudDirectorySystemKind.None);
    }

    [PostgresFact]
    public async Task ConcurrentNormalAndSystemCreate_UseOneFolder()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var barrier = new DirectoryWriteBarrier();
        var normal = Capture(() => Create(database, barrier, "Фото"));
        var system = Ensure(database, barrier, "Фото");

        await Task.WhenAll(normal, system);

        (await normal)?.Should().BeOfType<DirectoryNameConflictException>();
        await using var context = database.CreateContext();
        var directories = await new CloudHierarchyStorage(context).ListSubdirectories(OwnerId, null);
        var folder = directories.Should().ContainSingle().Subject;
        folder.Id.Should().Be(await system);
        folder.SystemKind.Should().Be(CloudDirectorySystemKind.Photos);
    }

    [PostgresFact]
    public async Task CanonicalNameOccupiedByOtherSystemKind_UsesSuffixWithoutChangingExistingKind()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var photos = Directory("Видео");
        photos.SystemKind = CloudDirectorySystemKind.Photos;
        await Seed(database, photos, Directory("Видео (1)"));
        await using var context = database.CreateContext();
        var storage = new CloudHierarchyStorage(context);

        var videos = await storage.EnsureSystemDirectory(OwnerId, CloudDirectorySystemKind.Videos, "Видео");

        (await storage.GetDirectoryAsNoTracking(videos))!.Name.Should().Be("Видео (2)");
        (await storage.GetDirectoryAsNoTracking(photos.Id))!.SystemKind.Should().Be(CloudDirectorySystemKind.Photos);
        (await storage.EnsureSystemDirectory(OwnerId, CloudDirectorySystemKind.Photos, "Фото")).Should().Be(photos.Id);
        (await storage.EnsureSystemDirectory(OwnerId, CloudDirectorySystemKind.Videos, "Видео")).Should().Be(videos);
    }

    [PostgresFact]
    public Task StaleRename_DoesNotResetPromotedSystemKind() => AssertStaleUpdatePreservesKind(move: false);

    [PostgresFact]
    public Task StaleMove_DoesNotResetPromotedSystemKind() => AssertStaleUpdatePreservesKind(move: true);

    private static async Task AssertStaleUpdatePreservesKind(bool move)
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var folder = Directory("Фото");
        var parent = Directory("Parent");
        await Seed(database, folder, parent);
        await using var context = database.CreateContext();
        var storage = new CloudHierarchyStorage(context);
        var stale = (await storage.GetDirectory(folder.Id))!;
        if (move)
            stale.ParentId = parent.Id;
        else
            stale.Name = "Renamed";
        stale.UpdatedAt = DateTime.UtcNow;
        await using (var otherContext = database.CreateContext())
            (await new CloudHierarchyStorage(otherContext).EnsureSystemDirectory(OwnerId, CloudDirectorySystemKind.Photos, "Фото"))
                .Should().Be(folder.Id);

        await storage.UpdateDirectory(stale);

        (await storage.GetDirectoryAsNoTracking(folder.Id))!.SystemKind.Should().Be(CloudDirectorySystemKind.Photos);
        (await storage.EnsureSystemDirectory(OwnerId, CloudDirectorySystemKind.Photos, "Фото")).Should().Be(folder.Id);
    }

    [PostgresFact]
    public Task Promotion_RechecksRenamedFolder() => AssertChangedPromotion("rename");

    [PostgresFact]
    public Task Promotion_RechecksMovedFolder() => AssertChangedPromotion("move");

    [PostgresFact]
    public Task Promotion_DoesNotOverwriteAnotherSystemKind() => AssertChangedPromotion("kind");

    private static async Task AssertChangedPromotion(string change)
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var folder = Directory("Фото");
        var parent = Directory("Parent");
        await Seed(database, folder, parent);
        var interceptor = new BeforeDirectoryWrite(async () =>
        {
            await using var otherContext = database.CreateContext();
            var otherStorage = new CloudHierarchyStorage(otherContext);
            var changed = (await otherStorage.GetDirectory(folder.Id))!;
            if (change == "rename")
                changed.Name = "Renamed";
            else if (change == "move")
                changed.ParentId = parent.Id;
            else
                changed.SystemKind = CloudDirectorySystemKind.Videos;
            await otherStorage.UpdateDirectory(changed);
        });
        await using var context = database.CreateContext(interceptor);
        var storage = new CloudHierarchyStorage(context);

        var id = await storage.EnsureSystemDirectory(OwnerId, CloudDirectorySystemKind.Photos, "Фото");

        id.Should().NotBe(folder.Id);
        var original = (await storage.GetDirectoryAsNoTracking(folder.Id))!;
        original.SystemKind.Should().Be(change == "kind" ? CloudDirectorySystemKind.Videos : CloudDirectorySystemKind.None);
        original.Name.Should().Be(change == "rename" ? "Renamed" : "Фото");
        original.ParentId.Should().Be(change == "move" ? parent.Id : null);
        (await storage.GetDirectoryAsNoTracking(id))!.Name.Should().Be(change == "kind" ? "Фото (1)" : "Фото");
    }

    [PostgresFact]
    public async Task Names_AreScopedToOwnerAndParent_AndNameConflictAllowsNextWrite()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var first = Directory("First");
        var second = Directory("Second");
        await Seed(database, first, second, Directory("Docs"), Directory("docs"), Directory("Docs", ownerId: 77),
            Directory("Docs", first.Id), Directory("Docs", second.Id));
        await using var context = database.CreateContext();
        var storage = new CloudHierarchyStorage(context);

        var duplicate = () => storage.AddDirectory(Directory("Docs", first.Id));
        await duplicate.Should().ThrowAsync<DirectoryNameConflictException>();
        await storage.AddDirectory(Directory("After"));

        (await storage.ListSubdirectories(OwnerId, first.Id)).Should().ContainSingle();
        (await storage.ListSubdirectories(OwnerId, second.Id)).Should().ContainSingle();
        (await storage.ListSubdirectories(77, null)).Should().ContainSingle();
        (await storage.ListSubdirectories(OwnerId, null)).Select(x => x.Name)
            .Should().BeEquivalentTo("First", "Second", "Docs", "docs", "After");
    }

    [PostgresFact]
    public async Task SystemKind_IsUniqueAcrossParentsAndNames_ButIndependentBetweenOwners()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var parent = Directory("Parent");
        var moved = Directory("Renamed", parent.Id);
        moved.SystemKind = CloudDirectorySystemKind.Photos;
        var otherOwner = Directory("Фото", ownerId: 77);
        otherOwner.SystemKind = CloudDirectorySystemKind.Photos;
        await Seed(database, parent, moved, otherOwner);
        await using var context = database.CreateContext();
        var storage = new CloudHierarchyStorage(context);

        (await storage.EnsureSystemDirectory(OwnerId, CloudDirectorySystemKind.Photos, "Фото")).Should().Be(moved.Id);
        var duplicate = Directory("Another");
        duplicate.SystemKind = CloudDirectorySystemKind.Photos;
        var insert = () => storage.AddDirectory(duplicate);
        var error = await insert.Should().ThrowAsync<DbUpdateException>();
        ((PostgresException)error.Which.InnerException!).ConstraintName.Should().Be("IX_CloudDirectories_OwnerId_SystemKind");
    }

    [PostgresFact]
    public async Task EnsureSystemDirectory_UnrelatedUniqueViolationIsNotRetried()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var storage = new CloudHierarchyStorage(context);
        var first = Entry(CloudHierarchyStorage.RootDirectoryId, "same.txt");
        CloudFileEntryFixtures.AddOriginals(context, [first]);
        await storage.AddFileEntry(first);
        var second = Entry(CloudHierarchyStorage.RootDirectoryId, "same.txt");
        CloudFileEntryFixtures.AddOriginals(context, [second]);
        context.CloudFileEntries.Add(second);

        var ensure = () => storage.EnsureSystemDirectory(OwnerId, CloudDirectorySystemKind.Photos, "Фото");
        var error = await ensure.Should().ThrowAsync<DbUpdateException>();

        ((PostgresException)error.Which.InnerException!).ConstraintName.Should().Be("IX_CloudFileEntries_OwnerId_DirectoryId_Name");
        await using var verification = database.CreateContext();
        (await new CloudHierarchyStorage(verification).ListSubdirectories(OwnerId, null)).Should().BeEmpty();
    }

    [PostgresFact]
    public async Task EnsureSystemDirectory_ObservesCancellation()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        await using var context = database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var ensure = () => new CloudHierarchyStorage(context)
            .EnsureSystemDirectory(OwnerId, CloudDirectorySystemKind.Photos, "Фото", cancellation.Token);

        await ensure.Should().ThrowAsync<OperationCanceledException>();
        (await new CloudHierarchyStorage(context).ListSubdirectories(OwnerId, null)).Should().BeEmpty();
    }

    private static CloudFileEntry Entry(Guid directoryId, string name) => new()
    {
        Id = Guid.NewGuid(), OwnerId = OwnerId, DirectoryId = directoryId,
        FileId = Guid.NewGuid(), Name = name, CreatedAt = DateTime.UtcNow
    };

    [PostgresFact]
    public Task ConcurrentRenameAndCreate_RenameGetsNameConflict() => AssertCreateAndUpdateConflict(move: false);

    [PostgresFact]
    public Task ConcurrentMoveAndCreate_MoveGetsNameConflict() => AssertCreateAndUpdateConflict(move: true);

    private static async Task AssertCreateAndUpdateConflict(bool move)
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var parent = Directory("Parent");
        var updated = Directory(move ? "Docs" : "Old", move ? parent.Id : null);
        await Seed(database, parent, updated);
        var barrier = new DirectoryWriteBarrier(insertFirst: true);

        var outcomes = await Task.WhenAll(
            Capture(() => Create(database, barrier, "Docs")),
            Capture(() => Update(database, barrier, updated.Id, move)));

        outcomes[0].Should().BeNull();
        outcomes[1].Should().BeOfType<DirectoryNameConflictException>();
        await using var context = database.CreateContext();
        var storage = new CloudHierarchyStorage(context);
        (await storage.ListSubdirectories(OwnerId, null)).Count(x => x.Name == "Docs").Should().Be(1);
        var unchanged = await storage.GetDirectoryAsNoTracking(updated.Id);
        unchanged!.Name.Should().Be(updated.Name);
        unchanged.ParentId.Should().Be(updated.ParentId);
    }

    private static async Task Update(PostgresFilesDatabase database, DirectoryWriteBarrier barrier, Guid id, bool move)
    {
        await using var context = database.CreateContext(barrier);
        var storage = new CloudHierarchyStorage(context);
        var user = UserContextFactory.Create(OwnerId);
        if (move)
            await new MoveDirectoryCommandHandler(storage, user, NullLogger<MoveDirectoryCommandHandler>.Instance)
                .Handle(new MoveDirectoryCommand { DirectoryId = id }, CancellationToken.None);
        else
            await new RenameDirectoryCommandHandler(storage, user, NullLogger<RenameDirectoryCommandHandler>.Instance)
                .Handle(new RenameDirectoryCommand { DirectoryId = id, NewName = "Docs" }, CancellationToken.None);
    }

    private static CloudDirectory Directory(string name, Guid? parentId = null, long ownerId = OwnerId) => new()
    {
        Id = Guid.NewGuid(), OwnerId = ownerId, ParentId = parentId, Name = name,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static async Task Seed(PostgresFilesDatabase database, params CloudDirectory[] directories)
    {
        await using var context = database.CreateContext();
        context.CloudDirectories.AddRange(directories);
        await context.SaveChangesAsync();
    }

    private static async Task<Guid> Ensure(PostgresFilesDatabase database, DirectoryWriteBarrier barrier, string name, bool transaction = false)
    {
        await using var context = database.CreateContext(barrier);
        await using var scope = transaction ? await context.Database.BeginTransactionAsync() : null;
        var storage = new CloudHierarchyStorage(context);
        var id = await storage.EnsureSystemDirectory(OwnerId, CloudDirectorySystemKind.Photos, name);
        var entry = Entry(id, $"{Guid.NewGuid():N}.txt");
        CloudFileEntryFixtures.AddOriginals(context, [entry]);
        await storage.AddFileEntry(entry);
        if (scope is not null)
            await scope.CommitAsync();
        return id;
    }

    // Мимо CreateDirectoryCommandHandler: он берёт замок дерева (F13) и не даёт двум созданиям одного владельца
    // дойти до записи одновременно. Здесь проверяются сами уникальные индексы — последняя линия защиты.
    private static async Task Create(PostgresFilesDatabase database, DirectoryWriteBarrier barrier, string name)
    {
        await using var context = database.CreateContext(barrier);
        await new CloudHierarchyStorage(context).AddDirectory(Directory(name));
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

    private sealed class AfterCanonicalLookup(Func<Task> action) : DbCommandInterceptor
    {
        private int _used;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"CloudDirectories\"", StringComparison.Ordinal) &&
                command.CommandText.Contains("\"ParentId\" IS NULL", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _used, 1) == 0)
                await action();
            return result;
        }
    }

    private sealed class BeforeDirectoryWrite(Func<Task> action) : DbCommandInterceptor
    {
        private int _used;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await Run(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await Run(command);
            return result;
        }

        private Task Run(DbCommand command)
        {
            var sql = command.CommandText.TrimStart();
            return sql.Contains("\"CloudDirectories\"", StringComparison.Ordinal) &&
                (sql.StartsWith("INSERT", StringComparison.Ordinal) || sql.StartsWith("UPDATE", StringComparison.Ordinal)) &&
                Interlocked.Exchange(ref _used, 1) == 0 ? action() : Task.CompletedTask;
        }
    }

    // Оба запроса уже прошли предварительное чтение; барьер отпускает их перед INSERT/UPDATE.
    // Каждый контекст участвует один раз, поэтому повтор после конфликта не застревает на барьере.
    private sealed class DirectoryWriteBarrier(bool insertFirst = false) : DbCommandInterceptor
    {
        private readonly HashSet<Guid> _contexts = [];
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _inserted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await Rendezvous(command, eventData, cancellationToken);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await Rendezvous(command, eventData, cancellationToken);
            return result;
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            MarkInserted(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            MarkInserted(command);
            return ValueTask.FromResult(result);
        }

        private void MarkInserted(DbCommand command)
        {
            if (command.CommandText.TrimStart().StartsWith("INSERT", StringComparison.Ordinal) &&
                command.CommandText.Contains("\"CloudDirectories\"", StringComparison.Ordinal))
                _inserted.TrySetResult();
        }

        private async Task Rendezvous(DbCommand command, CommandEventData eventData, CancellationToken cancellationToken)
        {
            var sql = command.CommandText.TrimStart();
            if (!sql.Contains("\"CloudDirectories\"", StringComparison.Ordinal) ||
                !(sql.StartsWith("INSERT", StringComparison.Ordinal) || sql.StartsWith("UPDATE", StringComparison.Ordinal)))
                return;

            lock (_contexts)
            {
                if (!_contexts.Add(eventData.Context!.ContextId.InstanceId))
                    return;
                if (_contexts.Count == 2)
                    _arrived.TrySetResult();
            }

            await _arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            if (insertFirst && sql.StartsWith("UPDATE", StringComparison.Ordinal))
                await _inserted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
    }
}
