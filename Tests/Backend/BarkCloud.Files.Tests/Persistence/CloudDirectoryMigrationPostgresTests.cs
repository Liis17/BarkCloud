using System.Text.RegularExpressions;

using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Npgsql;

namespace BarkCloud.Files.Tests.Persistence;

public sealed class CloudDirectoryMigrationPostgresTests
{
    private const string Migration = "20261002020415_EnforceUniqueCloudDirectories";
    private const string LegacyMigration = "20260913233127_PersistUploadSessionParts";
    private const long OwnerId = 42;

    [PostgresFact]
    public Task DuplicateRootNames_ReportIdsPreserveDataAndCanRetry() => AssertDuplicateMigration(system: false);

    [PostgresFact]
    public Task DuplicateSystemKinds_ReportIdsPreserveDataAndCanRetry() => AssertDuplicateMigration(system: true);

    [PostgresFact]
    public async Task UpDownUp_PreservesFoldersAndReferencesAndRestoresPreviousIndexes()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync(LegacyMigration);
        await using var context = database.CreateContext();
        var root = Directory("Docs");
        var photos = Directory("Renamed photos", CloudDirectorySystemKind.Photos);
        photos.ParentId = root.Id;
        var child = Directory("Docs");
        child.ParentId = photos.Id;
        var entry = new CloudFileEntry
        {
            Id = Guid.NewGuid(), OwnerId = OwnerId, DirectoryId = photos.Id, FileId = Guid.NewGuid(),
            Name = "file.txt", CreatedAt = DateTime.UtcNow
        };
        context.CloudDirectories.AddRange(root, photos, child);
        context.CloudFileEntries.Add(entry);
        context.FolderShareLinks.Add(new FolderShareLink
        {
            Id = Guid.NewGuid(), OwnerId = OwnerId, DirectoryId = photos.Id, Token = "roundtrip-folder-share",
            Name = photos.Name, CreatedAt = DateTime.UtcNow
        });
        context.DirectoryGrants.Add(new DirectoryGrant
        {
            Id = Guid.NewGuid(), OwnerId = OwnerId, DirectoryId = photos.Id, RecipientId = 77, CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        var directories = await context.CloudDirectories.AsNoTracking().ToListAsync();
        var entries = await context.CloudFileEntries.AsNoTracking().ToListAsync();
        var shares = await context.FolderShareLinks.AsNoTracking().ToListAsync();
        var grants = await context.DirectoryGrants.AsNoTracking().ToListAsync();

        await context.Database.MigrateAsync();
        await AssertUniqueIndexes(context);
        await context.GetService<IMigrator>().MigrateAsync(LegacyMigration);

        (await IndexDefinition(context, "IX_CloudDirectories_OwnerId_Name")).Should().BeNull();
        (await IndexDefinition(context, "IX_CloudDirectories_OwnerId_SystemKind")).Should().Contain("CREATE INDEX");
        (await IndexDefinition(context, "IX_CloudDirectories_OwnerId_ParentId_Name")).Should().Contain("CREATE UNIQUE INDEX");
        await context.Database.MigrateAsync();

        await AssertUniqueIndexes(context);
        (await context.CloudDirectories.AsNoTracking().ToListAsync()).Should().BeEquivalentTo(directories);
        (await context.CloudFileEntries.AsNoTracking().ToListAsync()).Should().BeEquivalentTo(entries);
        (await context.FolderShareLinks.AsNoTracking().ToListAsync()).Should().BeEquivalentTo(shares);
        (await context.DirectoryGrants.AsNoTracking().ToListAsync()).Should().BeEquivalentTo(grants);
    }

    [PostgresFact]
    public async Task DuplicateDiagnostics_ReportsAtMostTwentyGroupsOfEachKind()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync(LegacyMigration);
        await using var context = database.CreateContext();
        for (var owner = 1; owner <= 21; owner++)
        {
            context.CloudDirectories.AddRange(
                Directory("Docs", ownerId: owner), Directory("Docs", ownerId: owner),
                Directory("Photos A", CloudDirectorySystemKind.Photos, owner),
                Directory("Photos B", CloudDirectorySystemKind.Photos, owner));
        }
        await context.SaveChangesAsync();

        var migrate = () => context.Database.MigrateAsync();
        var error = await migrate.Should().ThrowAsync<PostgresException>();

        Regex.Matches(error.Which.MessageText, "OwnerId=").Count.Should().Be(40);
        error.Which.MessageText.Should().Contain("OwnerId=20,").And.NotContain("OwnerId=21,");
        (await context.CloudDirectories.CountAsync()).Should().Be(84);
    }

    private static async Task AssertDuplicateMigration(bool system)
    {
        await using var database = await PostgresFilesDatabase.CreateAsync(LegacyMigration);
        await using var context = database.CreateContext();
        var first = Directory("Docs", system ? CloudDirectorySystemKind.Photos : CloudDirectorySystemKind.None);
        var second = Directory(system ? "Pictures" : "Docs", first.SystemKind);
        var child = Directory("Child");
        child.ParentId = second.Id;
        var entry = new CloudFileEntry
        {
            Id = Guid.NewGuid(), OwnerId = OwnerId, DirectoryId = second.Id,
            FileId = Guid.NewGuid(), Name = "file.txt", CreatedAt = DateTime.UtcNow
        };
        var share = new FolderShareLink
        {
            Id = Guid.NewGuid(), OwnerId = OwnerId, DirectoryId = second.Id,
            Token = "duplicate-folder-share", Name = second.Name, CreatedAt = DateTime.UtcNow
        };
        var grant = new DirectoryGrant
        {
            Id = Guid.NewGuid(), OwnerId = OwnerId, RecipientId = 77, DirectoryId = second.Id,
            CreatedAt = DateTime.UtcNow
        };
        context.CloudDirectories.AddRange(first, second, child);
        context.CloudFileEntries.Add(entry);
        context.FolderShareLinks.Add(share);
        context.DirectoryGrants.Add(grant);
        await context.SaveChangesAsync();
        var before = await context.CloudDirectories.AsNoTracking().ToListAsync();

        var migrate = () => context.Database.MigrateAsync();
        var error = await migrate.Should().ThrowAsync<PostgresException>();

        error.Which.MessageText.Should().Contain("F16:").And.Contain("OwnerId=42")
            .And.Contain(first.Id.ToString()).And.Contain(second.Id.ToString());
        (await context.Database.GetAppliedMigrationsAsync()).Should().NotContain(Migration);
        (await IndexDefinition(context, "IX_CloudDirectories_OwnerId_Name")).Should().BeNull();
        (await IndexDefinition(context, "IX_CloudDirectories_OwnerId_SystemKind")).Should().Contain("CREATE INDEX");
        (await context.CloudDirectories.AsNoTracking().ToListAsync()).Should().BeEquivalentTo(before);
        var storage = new CloudHierarchyStorage(context);
        (await storage.GetFileEntry(entry.Id))!.DirectoryId.Should().Be(second.Id);
        (await context.FolderShareLinks.AsNoTracking().SingleAsync()).DirectoryId.Should().Be(second.Id);
        (await context.DirectoryGrants.AsNoTracking().SingleAsync()).DirectoryId.Should().Be(second.Id);

        // Исправление выполняет оператор; миграция сама не переименовывает и не объединяет папки.
        if (system)
            second.SystemKind = CloudDirectorySystemKind.None;
        else
            second.Name = "Docs (1)";
        await context.SaveChangesAsync();
        await context.Database.MigrateAsync();

        await AssertUniqueIndexes(context);
        (await storage.GetDirectoryAsNoTracking(child.Id))!.ParentId.Should().Be(second.Id);
        (await storage.GetFileEntry(entry.Id))!.DirectoryId.Should().Be(second.Id);
    }

    private static CloudDirectory Directory(string name, CloudDirectorySystemKind kind = CloudDirectorySystemKind.None,
        long ownerId = OwnerId) => new()
    {
        Id = Guid.NewGuid(), OwnerId = ownerId, Name = name, SystemKind = kind,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static Task<string?> IndexDefinition(FilesContext context, string index) =>
        context.Database.SqlQuery<string>(
            $"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE schemaname = 'public' AND indexname = {index}")
            .SingleOrDefaultAsync();

    private static async Task AssertUniqueIndexes(FilesContext context)
    {
        (await IndexDefinition(context, "IX_CloudDirectories_OwnerId_Name"))
            .Should().Contain("CREATE UNIQUE INDEX").And.Contain("\"ParentId\" IS NULL");
        (await IndexDefinition(context, "IX_CloudDirectories_OwnerId_SystemKind"))
            .Should().Contain("CREATE UNIQUE INDEX").And.Contain("\"SystemKind\" <> 0");
        (await IndexDefinition(context, "IX_CloudDirectories_OwnerId_ParentId_Name"))
            .Should().Contain("CREATE UNIQUE INDEX");
    }
}
