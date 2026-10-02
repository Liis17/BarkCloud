using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.AttachFile;
using BarkCloud.Files.Features.Cloud.CreateDirectory;
using BarkCloud.Files.Features.Cloud.CreateFolderShare;
using BarkCloud.Files.Features.Cloud.DeleteDirectory;
using BarkCloud.Files.Features.Cloud.MoveFileEntry;
using BarkCloud.Files.Features.Cloud.RestoreFromTrash;
using BarkCloud.Files.Features.Cloud.ShareFolderWithUser;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Tests.Persistence;

/// <summary>
/// F13: писатели, ссылающиеся на папку (создание подпапки, привязка и перенос файла, восстановление
/// из корзины, публичная ссылка и грант), против параллельного удаления этой папки на реальном PostgreSQL.
/// Писатель проверяет папку и замирает перед записью ссылки; затем стартует DeleteDirectory.
/// Без блокировки дерева удаление успевает завершиться, и писатель сохраняет ссылку на несуществующую папку.
/// С блокировкой удаление ждёт писателя, а затем забирает его запись вместе с поддеревом.
/// </summary>
public sealed class CloudTreeWriterRacePostgresTests
{
    private const long OwnerId = 42;
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(20);

    [PostgresFact]
    public async Task CreateDirectory_RacingDelete_LeavesNoOrphans()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var target = Directory("Target");
        await Seed(database, target);

        await RunRace(database, typeof(CloudDirectory), target.Id, context =>
            new CreateDirectoryCommandHandler(
                new CloudHierarchyStorage(context), UserContextFactory.Create(OwnerId),
                NullLogger<CreateDirectoryCommandHandler>.Instance)
                .Handle(new CreateDirectoryCommand { Name = "Child", ParentId = target.Id }, CancellationToken.None));

        await AssertNoOrphans(database);
    }

    [PostgresFact]
    public async Task AttachFile_RacingDelete_LeavesNoOrphans()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var target = Directory("Target");
        await Seed(database, target);
        var fileId = await SeedReadyFile(database, MediaKind.Document);

        await RunRace(database, typeof(CloudFileEntry), target.Id, context =>
            CreateAttach(context).Handle(
                new AttachFileCommand { FileId = fileId, Name = "f.txt", DirectoryId = target.Id }, CancellationToken.None));

        await AssertNoOrphans(database);
    }

    [PostgresFact]
    public async Task AttachFileRoutedToSystemFolder_RacingDelete_LeavesNoOrphans()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var photos = Directory("Фото", CloudDirectorySystemKind.Photos);
        await Seed(database, photos);
        var fileId = await SeedReadyFile(database, MediaKind.Photo);

        await RunRace(database, typeof(CloudFileEntry), photos.Id, context =>
            CreateAttach(context).Handle(
                new AttachFileCommand { FileId = fileId, Name = "p.jpg", RouteByMediaKind = true }, CancellationToken.None));

        await AssertNoOrphans(database);
    }

    [PostgresFact]
    public async Task MoveFileEntry_RacingDelete_LeavesNoOrphans()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var target = Directory("Target");
        await Seed(database, target);
        var entry = Entry(CloudHierarchyStorage.RootDirectoryId, "f.txt");
        await Seed(database, entry);

        await RunRace(database, typeof(CloudFileEntry), target.Id, context =>
            new MoveFileEntryCommandHandler(
                new CloudHierarchyStorage(context), UserContextFactory.Create(OwnerId),
                NullLogger<MoveFileEntryCommandHandler>.Instance)
                .Handle(new MoveFileEntryCommand { EntryId = entry.Id, NewDirectoryId = target.Id }, CancellationToken.None));

        await AssertNoOrphans(database);
    }

    [PostgresFact]
    public async Task RestoreFromTrash_RacingDelete_LeavesNoOrphans()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var target = Directory("Target");
        await Seed(database, target);
        var entry = Entry(target.Id, "f.txt");
        entry.IsDeleted = true;
        entry.DeletedAt = DateTime.UtcNow;
        entry.PurgeAt = DateTime.UtcNow.AddDays(14);
        await Seed(database, entry);

        await RunRace(database, typeof(CloudFileEntry), target.Id, context =>
            new RestoreFromTrashCommandHandler(
                new CloudHierarchyStorage(context), UserContextFactory.Create(OwnerId),
                NullLogger<RestoreFromTrashCommandHandler>.Instance)
                .Handle(new RestoreFromTrashCommand { EntryId = entry.Id }, CancellationToken.None));

        await AssertNoOrphans(database);
    }

    [PostgresFact]
    public async Task CreateFolderShare_RacingDelete_LeavesNoOrphans()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var target = Directory("Target");
        await Seed(database, target);

        await RunRace(database, typeof(FolderShareLink), target.Id, context =>
            new CreateFolderShareCommandHandler(
                new FolderShareStorage(context), new CloudHierarchyStorage(context), UserContextFactory.Create(OwnerId),
                NullLogger<CreateFolderShareCommandHandler>.Instance)
                .Handle(new CreateFolderShareCommand { DirectoryId = target.Id }, CancellationToken.None));

        await AssertNoOrphans(database);
    }

    [PostgresFact]
    public async Task ShareFolderWithUser_RacingDelete_LeavesNoOrphans()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        var target = Directory("Target");
        await Seed(database, target);

        await RunRace(database, typeof(DirectoryGrant), target.Id, context =>
            new ShareFolderWithUserCommandHandler(
                new DirectoryGrantStorage(context), new CloudHierarchyStorage(context), UserContextFactory.Create(OwnerId),
                NullLogger<ShareFolderWithUserCommandHandler>.Instance)
                .Handle(new ShareFolderWithUserCommand { DirectoryId = target.Id, RecipientUserId = 7 }, CancellationToken.None));

        await AssertNoOrphans(database);
    }

    /// <summary>
    /// Запускает писателя, ждёт, пока он дойдёт до записи ссылки (папка уже проверена), запускает удаление
    /// папки и даёт ему шанс завершиться раньше писателя. Без блокировки оно успевает; с блокировкой ждёт.
    /// Допустимый исход писателя — успех (удаление затем забрало запись) либо «папка не найдена».
    /// </summary>
    private static async Task RunRace(
        PostgresFilesDatabase database, Type writtenEntity, Guid deletedDirectoryId, Func<FilesContext, Task> writer)
    {
        var pause = new PauseBeforeSave(writtenEntity);
        var writerTask = Capture(async () =>
        {
            await using var context = database.CreateContext(pause);
            await writer(context);
        });
        if (await Task.WhenAny(pause.Reached, writerTask) == writerTask)
            throw new InvalidOperationException("Писатель завершился, не дойдя до записи ссылки.", await writerTask);

        var deleteTask = Capture(async () =>
        {
            await using var context = database.CreateContext();
            await new DeleteDirectoryCommandHandler(
                new CloudHierarchyStorage(context),
                new FolderShareStorage(context),
                new DirectoryGrantStorage(context),
                UserContextFactory.Create(OwnerId),
                NullLogger<DeleteDirectoryCommandHandler>.Instance)
                .Handle(new DeleteDirectoryCommand { DirectoryId = deletedDirectoryId }, CancellationToken.None);
        });
        await Task.WhenAny(deleteTask, Task.Delay(LockWait));

        pause.Release();
        var outcomes = await Task.WhenAll(writerTask, deleteTask);

        outcomes[1].Should().BeNull("удаление папки не должно завершаться ошибкой");
        if (outcomes[0] is not null)
            outcomes[0].Should().BeOfType<DirectoryNotFoundException>();
    }

    private static AttachFileCommandHandler CreateAttach(FilesContext context) => new(
        new CloudHierarchyStorage(context), new UploadedFilesStorage(context),
        UserContextFactory.Create(OwnerId), NullLogger<AttachFileCommandHandler>.Instance);

    private static async Task AssertNoOrphans(PostgresFilesDatabase database)
    {
        await using var context = database.CreateContext();

        var directories = await context.CloudDirectories.AsNoTracking()
            .Where(x => x.ParentId != null && !context.CloudDirectories.Any(p => p.Id == x.ParentId))
            .ToListAsync();
        directories.Should().BeEmpty("папка не должна указывать на уже удалённого родителя");

        var entries = await context.CloudFileEntries.AsNoTracking()
            .Where(x => !x.IsDeleted && x.DirectoryId != CloudHierarchyStorage.RootDirectoryId &&
                !context.CloudDirectories.Any(d => d.Id == x.DirectoryId))
            .ToListAsync();
        entries.Should().BeEmpty("живая запись не должна лежать в уже удалённой папке");

        var shares = await context.FolderShareLinks.AsNoTracking()
            .Where(x => !context.CloudDirectories.Any(d => d.Id == x.DirectoryId))
            .ToListAsync();
        shares.Should().BeEmpty("публичная ссылка не должна вести на удалённую папку");

        var grants = await context.DirectoryGrants.AsNoTracking()
            .Where(x => !context.CloudDirectories.Any(d => d.Id == x.DirectoryId))
            .ToListAsync();
        grants.Should().BeEmpty("грант не должен вести на удалённую папку");
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

    private static CloudDirectory Directory(string name, CloudDirectorySystemKind kind = CloudDirectorySystemKind.None) => new()
    {
        Id = Guid.NewGuid(),
        OwnerId = OwnerId,
        Name = name,
        SystemKind = kind,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static CloudFileEntry Entry(Guid directoryId, string name) => new()
    {
        Id = Guid.NewGuid(),
        OwnerId = OwnerId,
        DirectoryId = directoryId,
        FileId = Guid.NewGuid(),
        Name = name,
        CreatedAt = DateTime.UtcNow,
    };

    private static async Task Seed(PostgresFilesDatabase database, params object[] entities)
    {
        await using var context = database.CreateContext();
        context.AddRange(entities);
        await context.SaveChangesAsync();
    }

    private static async Task<Guid> SeedReadyFile(PostgresFilesDatabase database, MediaKind mediaKind)
    {
        var file = new UploadFile
        {
            Id = Guid.NewGuid(),
            Uploaders = [OwnerId],
            CreatedAt = DateTime.UtcNow,
            UploadedAt = DateTime.UtcNow,
            Etag = "etag",
            MediaKind = mediaKind,
        };
        await Seed(database, file);
        return file.Id;
    }

    /// <summary>
    /// Останавливает первое сохранение, в котором есть добавленная или изменённая сущность нужного типа:
    /// к этому моменту писатель уже проверил папку. Не отпущенный вовремя писатель падает по таймауту.
    /// </summary>
    private sealed class PauseBeforeSave(Type entityType) : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _used;

        public Task Reached => _reached.Task;

        public void Release() => _release.TrySetResult();

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var writes = eventData.Context!.ChangeTracker.Entries()
                .Any(x => x.State is EntityState.Added or EntityState.Modified && x.Entity.GetType() == entityType);
            if (writes && Interlocked.Exchange(ref _used, 1) == 0)
            {
                _reached.TrySetResult();
                await _release.Task.WaitAsync(Safety, cancellationToken);
            }

            return result;
        }
    }
}
