using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;

using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace BarkCloud.Files.IntegrationTests;

internal sealed class UploadTestDatabase : IAsyncDisposable
{
    private readonly string _admin;
    private readonly string _name = $"files_f18_{Guid.NewGuid():N}";

    public UploadTestDatabase()
    {
        var configured = Environment.GetEnvironmentVariable("BARKCLOUD_TEST_POSTGRES")
            ?? throw new InvalidOperationException("Set BARKCLOUD_TEST_POSTGRES to an isolated test PostgreSQL.");
        _admin = new NpgsqlConnectionStringBuilder(configured) { Database = "postgres", Pooling = false }.ConnectionString;
        ConnectionString = new NpgsqlConnectionStringBuilder(_admin) { Database = _name }.ConnectionString;
    }

    public string ConnectionString { get; }
    public FilesContext CreateContext() => new(new DbContextOptionsBuilder<FilesContext>().UseNpgsql(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(_admin);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{_name}\"", connection);
        await command.ExecuteNonQueryAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task<Guid> AddSessionAsync(UploadSessionStatus status = UploadSessionStatus.Processing)
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;
        var session = new UploadSession
        {
            Id = Guid.NewGuid(), FileId = Guid.NewGuid(), OwnerId = 42,
            IdempotencyKey = Guid.NewGuid().ToString("N"), FileName = "test.bin",
            DeclaredSize = 42, ContentType = "application/octet-stream", Sha256 = new string('a', 64),
            Status = status, StorageProfileId = "test", MultipartUploadId = "test", PartSize = 16 * 1024 * 1024,
            UploadTokenHash = string.Empty, ReservedBytes = 42, CreatedAt = now, UpdatedAt = now,
            LastActivityAt = now, ExpiresAt = now.AddDays(1), CompletedEtag = "etag", ConcurrencyToken = Guid.NewGuid()
        };
        context.UploadSessions.Add(session);
        context.UploadedFiles.Add(new UploadFile
        {
            Id = session.FileId, Uploaders = [42], CreatedAt = now, Type = UploadFileType.CloudFile,
            StorageProfileId = "test", Filename = "test.bin"
        });
        await context.SaveChangesAsync();
        return session.Id;
    }

    public async Task<UploadSession> ReadAsync(Guid id)
    {
        await using var context = CreateContext();
        return await context.UploadSessions.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    public async ValueTask DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(_admin);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}
