using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Npgsql;

namespace BarkCloud.Files.Tests.Persistence;

public sealed class CloudFileOriginalMigrationPostgresTests
{
    private const string LegacyMigration = "20261003125527_StorageWriteActivities";
    private const string Migration = "20261008052536_EnforceCloudFileOriginalIntegrity";

    [PostgresFact]
    public async Task UpDownUp_RemovesOnlyDanglingEntriesAndPreservesValidData()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync(LegacyMigration);
        await using var context = database.CreateContext();
        var original = new UploadFile
        {
            Id = Guid.NewGuid(), Uploaders = [42, 43], StorageProfileId = "test-storage",
            CreatedAt = DateTime.UtcNow, UploadedAt = DateTime.UtcNow, Etag = "etag",
        };
        var valid = new[] { Entry(original.Id, 42, false), Entry(original.Id, 43, true) };
        var missing = Guid.NewGuid();
        var invalid = new[] { Entry(missing, 42, false), Entry(missing, 43, true) };
        var favorite = new FavoriteFile { Id = Guid.NewGuid(), OwnerId = 42, FileId = missing, CreatedAt = DateTime.UtcNow };
        context.UploadedFiles.Add(original);
        context.CloudFileEntries.AddRange(valid.Concat(invalid));
        context.FavoriteFiles.Add(favorite);
        await context.SaveChangesAsync();
        var before = await context.CloudFileEntries.AsNoTracking().Where(e => e.FileId == original.Id).ToListAsync();

        await context.Database.MigrateAsync();

        (await context.CloudFileEntries.AsNoTracking().ToListAsync()).Should().BeEquivalentTo(before);
        (await context.UploadedFiles.CountAsync()).Should().Be(1);
        (await context.FavoriteFiles.SingleAsync()).Id.Should().Be(favorite.Id);
        await AssertConstraint(context);
        (await context.Database.GetAppliedMigrationsAsync()).Should().Contain(Migration);

        await context.GetService<IMigrator>().MigrateAsync(LegacyMigration);
        (await ConstraintCount(context)).Should().Be(0);
        (await IndexCount(context)).Should().Be(0);
        (await context.CloudFileEntries.AsNoTracking().ToListAsync()).Should().BeEquivalentTo(before);
        context.CloudFileEntries.Add(Entry(Guid.NewGuid(), 44, true));
        await context.SaveChangesAsync();

        await context.Database.MigrateAsync();
        (await context.CloudFileEntries.AsNoTracking().ToListAsync()).Should().BeEquivalentTo(before);
        await AssertConstraint(context);
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignKey_RejectsMissingOriginalAndRestrictsDeletion(bool deleted)
    {
        await using var database = await PostgresFilesDatabase.CreateAsync();
        await using var context = database.CreateContext();
        context.CloudFileEntries.Add(Entry(Guid.NewGuid(), 42, deleted));
        var insert = () => context.SaveChangesAsync();
        var error = await insert.Should().ThrowAsync<DbUpdateException>();
        error.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        context.ChangeTracker.Clear();
        var original = new UploadFile { Id = Guid.NewGuid(), StorageProfileId = "test-storage", CreatedAt = DateTime.UtcNow };
        context.UploadedFiles.Add(original);
        context.CloudFileEntries.Add(Entry(original.Id, 42, deleted));
        await context.SaveChangesAsync();

        var delete = () => context.UploadedFiles.Where(f => f.Id == original.Id).ExecuteDeleteAsync();
        (await delete.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.RestrictViolation);
        (await context.CloudFileEntries.CountAsync()).Should().Be(1);
        (await context.UploadedFiles.CountAsync()).Should().Be(1);
    }

    private static CloudFileEntry Entry(Guid fileId, long owner, bool deleted) => new()
    {
        Id = Guid.NewGuid(), OwnerId = owner, FileId = fileId, DirectoryId = Guid.Empty,
        Name = $"{fileId}.txt", CreatedAt = DateTime.UtcNow, IsDeleted = deleted,
        DeletedAt = deleted ? DateTime.UtcNow : null, PurgeAt = deleted ? DateTime.UtcNow.AddDays(14) : null,
    };

    private static async Task AssertConstraint(FilesContext context)
    {
        (await ConstraintCount(context)).Should().Be(1);
        (await IndexCount(context)).Should().Be(1);
        var deleteAction = await context.Database.SqlQuery<string>(
            $"SELECT confdeltype::text AS \"Value\" FROM pg_constraint WHERE conname = 'FK_CloudFileEntries_UploadedFiles_FileId' AND convalidated")
            .SingleAsync();
        deleteAction.Should().Be("r");
    }

    private static Task<int> ConstraintCount(FilesContext context) => context.Database.SqlQuery<int>(
        $"SELECT count(*)::integer AS \"Value\" FROM pg_constraint WHERE conname = 'FK_CloudFileEntries_UploadedFiles_FileId'").SingleAsync();

    private static Task<int> IndexCount(FilesContext context) => context.Database.SqlQuery<int>(
        $"SELECT count(*)::integer AS \"Value\" FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'IX_CloudFileEntries_FileId'").SingleAsync();
}
