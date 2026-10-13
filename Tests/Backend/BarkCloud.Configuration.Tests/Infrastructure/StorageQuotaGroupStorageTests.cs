using BarkCloud.Configuration.Domain;
using BarkCloud.Configuration.Infrastructure;
using BarkCloud.GrpcServer;
using BarkCloud.GrpcServer.Metrics;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Configuration.Tests.Infrastructure;

public sealed class StorageQuotaGroupStorageTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ConfigurationContext _context;
    private readonly StorageProfileStorage _profiles;
    private readonly StorageQuotaGroupStorage _storage;

    public StorageQuotaGroupStorageTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ConfigurationContext>()
            .UseSqlite(_connection)
            .Options;
        _context = new ConfigurationContext(options);
        _context.Database.EnsureCreated();
        _profiles = new StorageProfileStorage(_context, new MetricsCollector());
        _storage = new StorageQuotaGroupStorage(_context);
    }

    [Fact]
    public async Task Save_CreatesAndUpdatesGroup()
    {
        await _profiles.SaveAsync(Profile("images", "https://s3.example", "photos"), "admin", "web");
        var bucket = Bucket("https://s3.example", "photos");

        var created = await _storage.SaveAsync(null, " Photos ", 10, [bucket], "admin", "web");
        var updated = await _storage.SaveAsync(created.Id, "Archive", 20, [bucket], "editor", "settings");

        updated.Id.Should().Be(created.Id);
        updated.Name.Should().Be("Archive");
        updated.QuotaBytes.Should().Be(20);
        updated.Buckets.Should().ContainSingle(item => item.ServiceUrl == "https://s3.example"
            && item.BucketName == "photos");
        updated.EditedBy.Should().Be("editor");
        updated.EditedFrom.Should().Be("settings");
        updated.Id.Should().StartWith("grp-");
    }

    [Fact]
    public async Task Save_RejectsBucketAlreadyAssignedToAnotherGroup()
    {
        await _profiles.SaveAsync(Profile("images", "https://s3.example", "photos"), "admin", "web");
        var bucket = Bucket("https://s3.example", "photos");
        await _storage.SaveAsync(null, "Primary", 0, [bucket], "admin", "web");

        var act = () => _storage.SaveAsync(null, "Secondary", 0, [bucket], "admin", "web");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Бакет photos уже входит в группу Primary.");
    }

    [Fact]
    public async Task Save_RejectsUnknownNewBucket()
    {
        var act = () => _storage.SaveAsync(null, "Archive", 0,
            [Bucket("https://s3.example", "unknown")], "admin", "web");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Бакет unknown отсутствует в S3-профилях.");
    }

    [Fact]
    public async Task Save_AllowsPreviouslySavedBucketAfterItsProfileIsRemoved()
    {
        await _profiles.SaveAsync(Profile("images", "https://s3.example", "photos"), "admin", "web");
        var bucket = Bucket("https://s3.example", "photos");
        var group = await _storage.SaveAsync(null, "Archive", 0, [bucket], "admin", "web");
        var profiles = await _context.StorageProfiles.ToListAsync();
        _context.StorageProfileRevisions.RemoveRange(await _context.StorageProfileRevisions.ToListAsync());
        _context.StorageProfiles.RemoveRange(profiles);
        await _context.SaveChangesAsync();

        var updated = await _storage.SaveAsync(group.Id, "Archive", 0, [bucket], "admin", "web");

        updated.Buckets.Should().ContainSingle(item => item.BucketName == "photos");
    }

    [Fact]
    public async Task Save_RejectsDuplicateName()
    {
        await _storage.SaveAsync(null, "Archive", 0, [], "admin", "web");

        var act = () => _storage.SaveAsync(null, "Archive", 0, [], "admin", "web");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Группа с таким названием уже существует.");
    }

    [Fact]
    public async Task Save_RejectsNameLongerThan100Characters()
    {
        var act = () => _storage.SaveAsync(null, new string('a', 101), 0, [], "admin", "web");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Название группы не должно превышать 100 символов.");
    }

    [Fact]
    public async Task Save_RejectsNegativeQuota()
    {
        var act = () => _storage.SaveAsync(null, "Archive", -1, [], "admin", "web");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Квота группы не может быть отрицательной.");
    }

    [Fact]
    public async Task Save_RejectsDuplicateBucketInRequest()
    {
        await _profiles.SaveAsync(Profile("images", "https://s3.example", "photos"), "admin", "web");
        var bucket = Bucket("https://s3.example", "photos");

        var act = () => _storage.SaveAsync(null, "Archive", 0, [bucket, bucket], "admin", "web");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Бакет photos указан несколько раз.");
    }

    [Fact]
    public async Task Save_AllowsMovingBucketAfterRemovingItFromPreviousGroup()
    {
        await _profiles.SaveAsync(Profile("images", "https://s3.example", "photos"), "admin", "web");
        var bucket = Bucket("https://s3.example", "photos");
        var firstGroup = await _storage.SaveAsync(null, "Primary", 0, [bucket], "admin", "web");
        await _storage.SaveAsync(firstGroup.Id, "Primary", 0, [], "admin", "web");

        var secondGroup = await _storage.SaveAsync(null, "Secondary", 0, [bucket], "admin", "web");

        secondGroup.Buckets.Should().ContainSingle(item => item.BucketName == "photos");
        (await _storage.GetAllAsync()).Should().ContainSingle(group => group.Name == "Secondary");
    }

    [Fact]
    public async Task Delete_CascadesToBuckets()
    {
        await _profiles.SaveAsync(Profile("images", "https://s3.example", "photos"), "admin", "web");
        var group = await _storage.SaveAsync(null, "Archive", 0,
            [Bucket("https://s3.example", "photos")], "admin", "web");

        await _storage.DeleteAsync(group.Id);

        (await _storage.GetAllAsync()).Should().BeEmpty();
        (await _context.StorageQuotaGroupBuckets.ToListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Save_NormalizesR2EndpointUsingMatchedProfile()
    {
        await _profiles.SaveAsync(Profile("images", "http://account.r2.example:9000", "photos") with
        {
            IsR2 = true
        }, "admin", "web");
        var webEndpoint = S3Endpoint.Normalize("http://account.r2.example:9000", isR2: true);

        var group = await _storage.SaveAsync(null, "R2", 0,
            [Bucket(webEndpoint, "photos")], "admin", "web");

        group.Buckets.Should().ContainSingle(item => item.ServiceUrl == webEndpoint
            && item.BucketName == "photos");
    }

    private static StorageProfileInput Profile(string role, string serviceUrl, string bucket) => new(
        role,
        serviceUrl,
        "access",
        "secret",
        bucket,
        false,
        false,
        null,
        false);

    private static StorageQuotaGroupBucketInput Bucket(string serviceUrl, string bucketName) =>
        new(serviceUrl, bucketName);

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }
}
