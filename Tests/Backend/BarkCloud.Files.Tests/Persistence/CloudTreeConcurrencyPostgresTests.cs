using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.DeleteDirectory;
using BarkCloud.Files.Features.Cloud.MoveDirectory;
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
