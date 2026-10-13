using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.CreateDirectory;
using BarkCloud.Files.Features.Cloud.DeleteDirectory;
using BarkCloud.Files.Features.Cloud.MoveDirectory;
using BarkCloud.Files.Features.Cloud.SetDirectoryVisibility;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.Shared.Exceptions.Files;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Tests.Persistence;

/// <summary>
/// F13: конкурентные структурные изменения дерева папок на реальном PostgreSQL.
/// Оба хендлера останавливаются на барьере сразу после проверки и до записи: без блокировки дерева
/// оба прошли бы проверку по прежнему состоянию. Таймаут барьера нужен для случая, когда блокировка
/// корректно удерживает второй запрос — тогда первый просто идёт дальше один.
/// </summary>
public sealed class CloudTreeConcurrencyPostgresTests
{
    private const long OwnerId = 42;
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(1.5);

    [PostgresFact]
    public async Task ConcurrentCrossMoves_OneSucceedsAndOtherGetsCircular_NoCycle()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var a = Directory("A");
        var b = Directory("B");
        await Seed(database, a, b);

        using var gate = new CountdownEvent(2);
        var moveAIntoB = RunMove(database, gate, a.Id, b.Id);
        var moveBIntoA = RunMove(database, gate, b.Id, a.Id);
        var outcomes = await Task.WhenAll(Capture(moveAIntoB), Capture(moveBIntoA));

        outcomes.Count(x => x is null).Should().Be(1);
        outcomes.Single(x => x is not null).Should().BeOfType<CircularMoveException>();

        await using var context = database.CreateContext();
        var parents = await context.CloudDirectories.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.ParentId);
        (parents[a.Id] == b.Id && parents[b.Id] == a.Id).Should().BeFalse("дерево не должно содержать цикл");
        parents.Values.Count(x => x is null).Should().Be(1);
    }

    [PostgresFact]
    public async Task ConcurrentMoveIntoSubtreeAndDelete_LeavesNoOrphanDirectories()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var target = Directory("Target");
        var moved = Directory("Moved");
        await Seed(database, target, moved);

        using var gate = new CountdownEvent(2);
        var move = RunMove(database, gate, moved.Id, target.Id);
        var delete = RunDelete(database, gate, target.Id);
        var outcomes = await Task.WhenAll(Capture(move), Capture(delete));

        outcomes.Where(x => x is not null).Should().AllBeOfType<DirectoryNotFoundException>();

        await using var context = database.CreateContext();
        var orphans = await context.CloudDirectories.AsNoTracking()
            .Where(x => x.ParentId != null && !context.CloudDirectories.Any(p => p.Id == x.ParentId))
            .ToListAsync();
        orphans.Should().BeEmpty("папка не должна указывать на уже удалённого родителя");
    }

    [PostgresFact]
    public async Task SetVisibility_RacingCreateChild_PreservesInheritance()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var parent = Directory("Parent");
        await Seed(database, parent);

        await RunConcurrently(
            RunSetVisibility(database, parent.Id, inherit: false, hideFromGallery: true, hideFromSearch: true),
            RunCreateDirectory(database, parent.Id, "Child"));

        await AssertVisibilityConsistent(database);
        await using var context = database.CreateContext();
        var child = await context.CloudDirectories.AsNoTracking().SingleAsync(x => x.ParentId == parent.Id);
        child.EffectiveHideFromGallery.Should().BeTrue();
        child.EffectiveHideFromSearch.Should().BeTrue();
    }

    [PostgresFact]
    public async Task SetVisibility_RacingMoveSubtree_PreservesInheritance()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var parent = Directory("Parent");
        var moved = Directory("Moved");
        var child = Directory("Child");
        child.ParentId = moved.Id;
        await Seed(database, parent, moved, child);

        await RunConcurrently(
            RunSetVisibility(database, parent.Id, inherit: false, hideFromGallery: true, hideFromSearch: true),
            RunMoveDirectory(database, moved.Id, parent.Id));

        await AssertVisibilityConsistent(database);
        await using var context = database.CreateContext();
        var persisted = await context.CloudDirectories.AsNoTracking().ToDictionaryAsync(x => x.Id);
        persisted[moved.Id].ParentId.Should().Be(parent.Id);
        persisted[moved.Id].EffectiveHideFromGallery.Should().BeTrue();
        persisted[child.Id].EffectiveHideFromGallery.Should().BeTrue();
        persisted[moved.Id].EffectiveHideFromSearch.Should().BeTrue();
        persisted[child.Id].EffectiveHideFromSearch.Should().BeTrue();
    }

    [PostgresFact]
    public async Task SetVisibility_RacingIntermediateOverride_PreservesOverride()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var parent = Directory("Parent");
        var intermediate = Directory("Intermediate");
        intermediate.ParentId = parent.Id;
        intermediate.InheritVisibility = false;
        var child = Directory("Child");
        child.ParentId = intermediate.Id;
        await Seed(database, parent, intermediate, child);

        await RunConcurrently(
            RunSetVisibility(database, parent.Id, inherit: false, hideFromGallery: true, hideFromSearch: true),
            RunSetVisibility(database, intermediate.Id, inherit: false, hideFromGallery: false, hideFromSearch: false));

        await AssertVisibilityConsistent(database);
        await using var context = database.CreateContext();
        var persisted = await context.CloudDirectories.AsNoTracking().ToDictionaryAsync(x => x.Id);
        persisted[parent.Id].EffectiveHideFromGallery.Should().BeTrue();
        persisted[parent.Id].EffectiveHideFromSearch.Should().BeTrue();
        persisted[intermediate.Id].InheritVisibility.Should().BeFalse();
        persisted[intermediate.Id].EffectiveHideFromGallery.Should().BeFalse();
        persisted[intermediate.Id].EffectiveHideFromSearch.Should().BeFalse();
        persisted[child.Id].EffectiveHideFromGallery.Should().BeFalse();
        persisted[child.Id].EffectiveHideFromSearch.Should().BeFalse();
    }

    private static Func<Task> RunMove(PostgresFilesDatabase database, CountdownEvent gate, Guid directoryId, Guid newParentId) =>
        () => Task.Run(async () =>
        {
            await using var context = database.CreateContext();
            var storage = Forwarding(new CloudHierarchyStorage(context), gate);
            var handler = new MoveDirectoryCommandHandler(
                storage, UserContextFactory.Create(OwnerId), NullLogger<MoveDirectoryCommandHandler>.Instance);
            await handler.Handle(
                new MoveDirectoryCommand { DirectoryId = directoryId, NewParentId = newParentId }, CancellationToken.None);
        });

    private static Func<Task> RunDelete(PostgresFilesDatabase database, CountdownEvent gate, Guid directoryId) =>
        () => Task.Run(async () =>
        {
            await using var context = database.CreateContext();
            var storage = Forwarding(new CloudHierarchyStorage(context), gate);
            var handler = new DeleteDirectoryCommandHandler(
                storage,
                new FolderShareStorage(context),
                new DirectoryGrantStorage(context),
                UserContextFactory.Create(OwnerId),
                NullLogger<DeleteDirectoryCommandHandler>.Instance);
            await handler.Handle(new DeleteDirectoryCommand { DirectoryId = directoryId }, CancellationToken.None);
        });

    private static Func<Task> RunSetVisibility(
        PostgresFilesDatabase database,
        Guid directoryId,
        bool inherit,
        bool hideFromGallery,
        bool hideFromSearch) =>
        RunWithContext(database, context => new SetDirectoryVisibilityCommandHandler(
                new CloudHierarchyStorage(context), UserContextFactory.Create(OwnerId))
            .Handle(new SetDirectoryVisibilityCommand
            {
                DirectoryId = directoryId,
                Inherit = inherit,
                HideFromGallery = hideFromGallery,
                HideFromSearch = hideFromSearch
            }, CancellationToken.None));

    private static Func<Task> RunCreateDirectory(PostgresFilesDatabase database, Guid parentId, string name) =>
        RunWithContext(database, context => new CreateDirectoryCommandHandler(
                new CloudHierarchyStorage(context), UserContextFactory.Create(OwnerId),
                NullLogger<CreateDirectoryCommandHandler>.Instance)
            .Handle(new CreateDirectoryCommand { Name = name, ParentId = parentId }, CancellationToken.None));

    private static Func<Task> RunMoveDirectory(PostgresFilesDatabase database, Guid directoryId, Guid parentId) =>
        RunWithContext(database, context => new MoveDirectoryCommandHandler(
                new CloudHierarchyStorage(context), UserContextFactory.Create(OwnerId),
                NullLogger<MoveDirectoryCommandHandler>.Instance)
            .Handle(new MoveDirectoryCommand { DirectoryId = directoryId, NewParentId = parentId }, CancellationToken.None));

    private static Func<Task> RunWithContext(PostgresFilesDatabase database, Func<FilesContext, Task> action) =>
        async () =>
        {
            await using var context = database.CreateContext();
            await action(context);
        };

    private static async Task RunConcurrently(params Func<Task>[] operations)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = operations.Select(operation => Task.Run(async () =>
        {
            await start.Task;
            await operation();
        })).ToArray();

        start.SetResult();
        await Task.WhenAll(tasks);
    }

    private static async Task AssertVisibilityConsistent(PostgresFilesDatabase database)
    {
        await using var context = database.CreateContext();
        var directories = await context.CloudDirectories.AsNoTracking().ToDictionaryAsync(x => x.Id);
        foreach (var directory in directories.Values)
        {
            var parent = directory.ParentId is Guid parentId ? directories[parentId] : null;
            var expectedGallery = directory.InheritVisibility && parent is not null
                ? parent.EffectiveHideFromGallery
                : directory.HideFromGallery;
            var expectedSearch = directory.InheritVisibility && parent is not null
                ? parent.EffectiveHideFromSearch
                : directory.HideFromSearch;

            directory.EffectiveHideFromGallery.Should().Be(expectedGallery);
            directory.EffectiveHideFromSearch.Should().Be(expectedSearch);
        }
    }

    /// <summary>
    /// Передаёт вызовы настоящему хранилищу; после проверки (Move: DirectoryNameExists) и после
    /// снимка поддерева (Delete: GetFileEntriesInDirectories) ждёт второй хендлер на барьере.
    /// </summary>
    private static ICloudHierarchyStorage Forwarding(CloudHierarchyStorage inner, CountdownEvent gate)
    {
        void Rendezvous()
        {
            gate.Signal();
            gate.Wait(BarrierTimeout);
        }

        var storage = new Mock<ICloudHierarchyStorage>();
        storage.Setup(s => s.LockTree(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns((long owner, CancellationToken ct) => inner.LockTree(owner, ct));
        storage.Setup(s => s.GetDirectory(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid id, CancellationToken ct) => inner.GetDirectory(id, ct));
        storage.Setup(s => s.GetDirectoryAsNoTracking(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid id, CancellationToken ct) => inner.GetDirectoryAsNoTracking(id, ct));
        storage.Setup(s => s.DirectoryNameExists(It.IsAny<long>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((long owner, Guid? parent, string name, CancellationToken ct) =>
            {
                Rendezvous();
                return inner.DirectoryNameExists(owner, parent, name, ct);
            });
        storage.Setup(s => s.UpdateDirectory(It.IsAny<CloudDirectory>(), It.IsAny<CancellationToken>()))
            .Returns((CloudDirectory d, CancellationToken ct) => inner.UpdateDirectory(d, ct));
        storage.Setup(s => s.GetSubtree(It.IsAny<long>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((long owner, Guid root, CancellationToken ct) => inner.GetSubtree(owner, root, ct));
        storage.Setup(s => s.GetFileEntriesInDirectories(It.IsAny<long>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .Returns((long owner, IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
            {
                Rendezvous();
                return inner.GetFileEntriesInDirectories(owner, ids, ct);
            });
        storage.Setup(s => s.RemoveDirectories(It.IsAny<IEnumerable<CloudDirectory>>()))
            .Callback((IEnumerable<CloudDirectory> dirs) => inner.RemoveDirectories(dirs));
        storage.Setup(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => inner.SaveChangesAsync(ct));
        return storage.Object;
    }

    private static async Task<Exception?> Capture(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static CloudDirectory Directory(string name) => new()
    {
        Id = Guid.NewGuid(),
        OwnerId = OwnerId,
        Name = name,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static async Task Seed(PostgresFilesDatabase database, params CloudDirectory[] directories)
    {
        await using var context = database.CreateContext();
        context.CloudDirectories.AddRange(directories);
        await context.SaveChangesAsync();
    }
}
