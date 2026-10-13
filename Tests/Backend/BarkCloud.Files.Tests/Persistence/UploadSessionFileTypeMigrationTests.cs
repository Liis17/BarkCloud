using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Persistence.Migrations;
using BarkCloud.Files.Tests._Helpers;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Tests.Persistence;

public sealed class UploadSessionFileTypeMigrationTests
{
    private const string PreviousMigration = "20261009000000_AddUploadScheduler";

    [Fact]
    public void Snapshot_ConfiguresCloudFileAsTheDatabaseDefault()
    {
        var model = new FilesContextModelSnapshot().Model;
        var property = model.FindEntityType(typeof(UploadSession))!
            .FindProperty(nameof(UploadSession.FileType))!;

        Convert.ToInt32(property.GetDefaultValue()).Should().Be((int)UploadFileType.CloudFile);
    }

    [PostgresFact]
    public async Task Migration_BackfillsValidUploadedFileTypesAndDefaultsMissingOrUnknownFilesToCloudFile()
    {
        await using var database = await PostgresFilesDatabase.CreateAsync(PreviousMigration);
        await using var context = database.CreateContext();
        var now = DateTime.UtcNow;
        var avatarFileId = Guid.NewGuid();
        var cloudFileId = Guid.NewGuid();
        var unknownFileId = Guid.NewGuid();
        var missingFileId = Guid.NewGuid();
        var avatarSessionId = Guid.NewGuid();
        var cloudSessionId = Guid.NewGuid();
        var unknownSessionId = Guid.NewGuid();
        var missingSessionId = Guid.NewGuid();

        await InsertFile(context, avatarFileId, (int)UploadFileType.UserAvatar, now);
        await InsertFile(context, cloudFileId, (int)UploadFileType.CloudFile, now);
        await InsertFile(context, unknownFileId, (int)UploadFileType.Unknown, now);
        await InsertSession(context, avatarSessionId, avatarFileId, now);
        await InsertSession(context, cloudSessionId, cloudFileId, now);
        await InsertSession(context, unknownSessionId, unknownFileId, now);
        await InsertSession(context, missingSessionId, missingFileId, now);

        await context.Database.MigrateAsync();

        await AssertType(context, avatarSessionId, UploadFileType.UserAvatar);
        await AssertType(context, cloudSessionId, UploadFileType.CloudFile);
        await AssertType(context, unknownSessionId, UploadFileType.CloudFile);
        await AssertType(context, missingSessionId, UploadFileType.CloudFile);
    }

    private static Task InsertFile(FilesContext context, Guid fileId, int type, DateTime now) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "UploadedFiles"
                ("Id", "Uploaders", "CreatedAt", "UploadedAt", "Etag", "Type", "StorageProfileId", "MediaKind", "Filename", "UploadDeviceName", "Size", "ImageWidth", "ImageHeight", "JpegViewFileId")
            VALUES
                ({fileId}, ARRAY[42::bigint], {now}, NULL, NULL, {type}, 'test-profile', 0, 'file.bin', NULL, 0, NULL, NULL, NULL)
            """);

    private static Task InsertSession(FilesContext context, Guid sessionId, Guid fileId, DateTime now) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "UploadSessions"
                ("Id", "FileId", "OwnerId", "IdempotencyKey", "FileName", "DeclaredSize", "ContentType", "Sha256", "Status", "StorageProfileId", "MultipartUploadId", "PartSize", "UploadTokenHash", "ReservedBytes", "CreatedAt", "UpdatedAt", "LastActivityAt", "ExpiresAt", "ProcessingAttempts", "CleanupPending", "ConcurrencyToken")
            VALUES
                ({sessionId}, {fileId}, 42, {sessionId.ToString("N")}, 'file.bin', 1, 'application/octet-stream', {new string('a', 64)}, 1, 'test-profile', 'multipart', 16777216, {new string('b', 64)}, 1, {now}, {now}, {now}, {now.AddHours(1)}, 0, false, {Guid.NewGuid()})
            """);

    private static async Task AssertType(FilesContext context, Guid sessionId, UploadFileType expected)
    {
        var value = await context.Database.SqlQuery<int>(
            $"SELECT \"FileType\" AS \"Value\" FROM \"UploadSessions\" WHERE \"Id\" = {sessionId}")
            .SingleAsync();
        value.Should().Be((int)expected);
    }
}
