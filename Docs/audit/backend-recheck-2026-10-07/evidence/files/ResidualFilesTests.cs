using System.Text.Json;
using BarkCloud.Files.Domain;
using BarkCloud.Files.Features.Cloud.AttachFile;
using BarkCloud.Files.Features.Cloud.DeleteDirectory;
using BarkCloud.Files.Features.Cloud.RenameFileEntry;
using BarkCloud.Files.Infrastructure;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.Files.Tests._Helpers;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.Proto.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Xunit;
using Xunit.Abstractions;
using MediaKind = BarkCloud.Files.Domain.MediaKind;
using UploadFileType = BarkCloud.Files.Domain.UploadFileType;

public sealed class ResidualFilesTests(ITestOutputHelper output)
{
    private const long Owner = 42;
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task F13_RenameMustNotResurrectEntryAfterDeleteDirectory()
    {
        await using var db = await PostgresFilesDatabase.CreateAsync();
        var directory = Directory("parent");
        var file = File();
        var entry = Entry(file.Id, directory.Id);
        await Seed(db, directory, file, entry);
        var pause = new PauseBeforeEntrySave();
        await using var renameContext = db.CreateContext(pause);
        var rename = new RenameFileEntryCommandHandler(new CloudHierarchyStorage(renameContext), UserContextFactory.Create(Owner),
            NullLogger<RenameFileEntryCommandHandler>.Instance).Handle(new RenameFileEntryCommand { EntryId = entry.Id, NewName = "renamed" }, default);
        await pause.Reached.WaitAsync(Safety);
        try
        {
            await using var deleteContext = db.CreateContext();
            var delete = new DeleteDirectoryCommandHandler(new CloudHierarchyStorage(deleteContext),
                new FolderShareStorage(deleteContext), new DirectoryGrantStorage(deleteContext), UserContextFactory.Create(Owner),
                NullLogger<DeleteDirectoryCommandHandler>.Instance);
            await delete.Handle(new DeleteDirectoryCommand { DirectoryId = directory.Id }, default).WaitAsync(Safety);
        }
        finally { pause.Release(); }
        await rename.WaitAsync(Safety);
        await using var verify = db.CreateContext();
        var actual = await verify.CloudFileEntries.SingleAsync(e => e.Id == entry.Id);
        var directoryExists = await verify.CloudDirectories.AnyAsync(d => d.Id == directory.Id);
        output.WriteLine(JsonSerializer.Serialize(new { directoryExists, actual.IsDeleted, actual.Name, actual.DirectoryId, actual.DeletedAt, actual.PurgeAt }));
        Assert.False(directoryExists);
        Assert.True(actual.IsDeleted, "Rename resurrected a live entry referring to a deleted directory.");
    }

    [Fact]
    public async Task F15_AttachMustNotSucceedAfterOriginalWasPurged()
    {
        await using var db = await PostgresFilesDatabase.CreateAsync();
        var file = File();
        var entry = Entry(file.Id, Guid.Empty);
        entry.IsDeleted = true;
        entry.DeletedAt = DateTime.UtcNow.AddDays(-15);
        entry.PurgeAt = DateTime.UtcNow.AddDays(-1);
        await Seed(db, file, entry);
        var pause = new PauseBeforeEntrySave();
        await using var attachContext = db.CreateContext(pause);
        var attach = new AttachFileCommandHandler(new CloudHierarchyStorage(attachContext), new UploadedFilesStorage(attachContext),
            UserContextFactory.Create(Owner), NullLogger<AttachFileCommandHandler>.Instance)
            .Handle(new AttachFileCommand { FileId = file.Id, Name = "new-live-entry" }, default);
        await pause.Reached.WaitAsync(Safety);
        var deletedKeys = new List<string>();
        await using var purgeContext = db.CreateContext();
        await purgeContext.Database.OpenConnectionAsync();
        var registry = new Mock<S3BucketRegistry>(TestConfiguration.Empty()) { CallBase = false };
        registry.Setup(r => r.ResolveReadProfileId(It.IsAny<UploadFile>())).Returns("test-storage");
        var s3 = new Mock<S3Uploader>(registry.Object) { CallBase = false };
        s3.Setup(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((_, key) => deletedKeys.Add(key)).Returns(Task.CompletedTask);
        var hashes = new Mock<IFileHashesStorage>();
        hashes.Setup(h => h.DeleteHashByFileId(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
        var purge = new TrashPurgeService(purgeContext, s3.Object, registry.Object, hashes.Object, NullLogger<TrashPurgeService>.Instance);
        var snapshot = await purgeContext.CloudFileEntries.AsNoTracking().Where(e => e.Id == entry.Id).ToListAsync();
        var purgeTask = purge.PurgeEntriesAsync(snapshot, default);
        var waited = false;
        try
        {
            using var timeout = new CancellationTokenSource(Safety);
            await using var connection = await db.DataSource.OpenConnectionAsync(timeout.Token);
            await using var command = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE pid = $1 AND locktype = 'advisory' AND NOT granted)", connection);
            command.Parameters.AddWithValue(((NpgsqlConnection)purgeContext.Database.GetDbConnection()).ProcessID);
            while (!purgeTask.IsCompleted)
            {
                if (await command.ExecuteScalarAsync(timeout.Token) is true)
                {
                    waited = true;
                    break;
                }
                await Task.Delay(10, timeout.Token);
            }
        }
        finally
        {
            pause.Release();
            await Task.WhenAll(attach, purgeTask).WaitAsync(Safety);
        }
        var result = await purgeTask;
        output.WriteLine(JsonSerializer.Serialize(new { result.Entries, result.Blobs, deletedKeys, waited }));
        await using var verify = db.CreateContext();
        var blobExists = await verify.UploadedFiles.AnyAsync(f => f.Id == file.Id);
        var liveEntries = await verify.CloudFileEntries.CountAsync(e => e.FileId == file.Id && !e.IsDeleted);
        output.WriteLine(JsonSerializer.Serialize(new { attachSucceeded = true, blobExists, liveEntries }));
        Assert.True(blobExists, "Attach succeeded with a live entry referencing a missing original.");
        Assert.Equal(1, liveEntries);
        Assert.True(waited, "Purge must wait for Attach to commit.");
        Assert.DoesNotContain(file.Id.ToString(), deletedKeys);
        Assert.Equal(0, result.Blobs);
    }

    [Fact]
    public async Task F20_SearchEveryReturnedFolderMustHaveMatchingField()
    {
        await using var db = await PostgresFilesDatabase.CreateAsync();
        var folder = Directory("report");
        await Seed(db, folder);
        await using var context = db.CreateContext();
        const string query = "quarterly report finances";
        var service = new UnifiedSearchService(context, UserContextFactory.Create(Owner), new RunSettings { Host = "http://localhost", Http1Port = 7026 }, TestConfiguration.Empty());
        var request = new SearchRequest { Query = query };
        request.Pages.Add(new SearchSectionPage { Section = SearchSection.Folders, Limit = 20 });
        var response = await service.Search(request, default);
        var hit = response.Sections.Single().Hits.Single(h => h.Id == folder.Id.ToString());
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT word_similarity('report', 'quarterly report finances')", connection);
        var sqlSimilarity = await command.ExecuteScalarAsync();
        output.WriteLine(JsonSerializer.Serialize(new { query, hit.Title, hit.MatchField, hit.MatchValue, sqlSimilarity }));
        Assert.Equal("name", hit.MatchField);
        Assert.Equal("report", hit.MatchValue);
    }

    private static CloudDirectory Directory(string name) => new() { Id = Guid.NewGuid(), OwnerId = Owner, Name = name, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
    private static UploadFile File() => new() { Id = Guid.NewGuid(), Uploaders = [Owner], CreatedAt = DateTime.UtcNow, UploadedAt = DateTime.UtcNow, Etag = "etag", Type = UploadFileType.CloudFile, MediaKind = MediaKind.Document, StorageProfileId = "test-storage" };
    private static CloudFileEntry Entry(Guid file, Guid directory) => new() { Id = Guid.NewGuid(), FileId = file, DirectoryId = directory, OwnerId = Owner, Name = "original", CreatedAt = DateTime.UtcNow };
    private static async Task Seed(PostgresFilesDatabase db, params object[] entities) { await using var context = db.CreateContext(); context.AddRange(entities); await context.SaveChangesAsync(); }

    private sealed class PauseBeforeEntrySave : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _used;
        public Task Reached => _reached.Task;
        public void Release() => _release.TrySetResult();
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<CloudFileEntry>().Any(e => e.State is EntityState.Added or EntityState.Modified) && Interlocked.Exchange(ref _used, 1) == 0)
            { _reached.TrySetResult(); await _release.Task.WaitAsync(Safety, cancellationToken); }
            return result;
        }
    }
}
