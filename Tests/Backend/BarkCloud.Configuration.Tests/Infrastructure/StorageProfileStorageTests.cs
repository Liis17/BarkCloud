using BarkCloud.Configuration.Domain;
using BarkCloud.Configuration.Infrastructure;
using BarkCloud.GrpcServer.Metrics;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Configuration.Tests.Infrastructure;

public sealed class StorageProfileStorageTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ConfigurationContext _context;
    private readonly StorageProfileStorage _storage;

    public StorageProfileStorageTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ConfigurationContext>()
            .UseSqlite(_connection)
            .Options;
        _context = new ConfigurationContext(options);
        _context.Database.EnsureCreated();
        _storage = new StorageProfileStorage(_context, new MetricsCollector());
    }

    [Fact]
    public async Task Save_LocationChange_CreatesNextVersionAndKeepsPreviousProfile()
    {
        var first = await _storage.SaveAsync(Input("images", "https://one.example", "bucket"), "admin", "web");
        var second = await _storage.SaveAsync(Input("images", "https://two.example", "bucket"), "admin", "web");

        first.ProfileId.Should().Be("images-v1");
        second.ProfileId.Should().Be("images-v2");
        (await _storage.GetAllAsync()).Should().ContainSingle(x => x.ProfileId == "images-v1" && !x.IsActive);
        (await _storage.GetAllAsync()).Should().ContainSingle(x => x.ProfileId == "images-v2" && x.IsActive);
        (await _storage.GetHistoryAsync("images-v1", 10)).Should().ContainSingle(x => x.ChangeKind == "Superseded");
        (await _storage.GetHistoryAsync("images-v2", 10)).Should().ContainSingle(x => x.ChangeKind == "NewVersion");
    }

    [Fact]
    public async Task Save_CredentialRotation_UpdatesEveryVersionAtTheSamePhysicalLocation()
    {
        await _storage.SaveAsync(Input("images", "https://s3.example", "shared"), "admin", "web");
        await _storage.SaveAsync(Input("videos", "https://s3.example", "shared"), "admin", "web");

        await _storage.SaveAsync(Input("images", "https://s3.example", "shared") with
        {
            AccessKey = "rotated",
            SecretKey = "rotated-secret"
        }, "admin", "web");

        (await _storage.GetAllAsync()).Where(x => x.BucketName == "shared")
            .Should().OnlyContain(x => x.AccessKey == "rotated" && x.SecretKey == "rotated-secret");
    }

    [Fact]
    public async Task Save_EmptySecret_KeepsCurrentSecret()
    {
        await _storage.SaveAsync(Input("audio", "https://s3.example", "audio"), "admin", "web");

        var saved = await _storage.SaveAsync(Input("audio", "https://s3.example", "audio") with
        {
            AccessKey = "new-key",
            SecretKey = ""
        }, "admin", "web");

        saved.SecretKey.Should().Be("secret");
    }

    [Fact]
    public async Task Save_DefaultQuotaIsUnlimited()
    {
        var saved = await _storage.SaveAsync(Input("audio", "https://s3.example", "audio"), "admin", "web");

        saved.QuotaBytes.Should().Be(0);
    }

    [Fact]
    public async Task Save_QuotaChangeCreatesRevisionAndSynchronizesProfilesForTheSameBucket()
    {
        const long firstQuota = 10L * 1024 * 1024 * 1024;
        const long secondQuota = 20L * 1024 * 1024 * 1024;
        await _storage.SaveAsync(Input("images", "https://s3.example", "shared", firstQuota), "admin", "web");
        await _storage.SaveAsync(Input("previews", "https://s3.example/", "shared", firstQuota), "admin", "web");
        await _storage.SaveAsync(Input("videos", "https://s3.example", "other", 7), "admin", "web");

        await _storage.SaveAsync(Input("images", "https://s3.example", "shared", secondQuota), "admin", "web");

        (await _storage.GetAllAsync()).Where(profile => profile.BucketName == "shared")
            .Should().OnlyContain(profile => profile.QuotaBytes == secondQuota);
        (await _storage.GetAllAsync()).Single(profile => profile.BucketName == "other")
            .QuotaBytes.Should().Be(7);
        foreach (var profileId in new[] { "images-v1", "previews-v1" })
            (await _storage.GetHistoryAsync(profileId, 10)).Should().ContainSingle(revision => revision.ChangeKind == "QuotaChange");
    }

    [Fact]
    public async Task Save_NegativeQuotaIsRejected()
    {
        var act = () => _storage.SaveAsync(Input("audio", "https://s3.example", "audio", -1), "admin", "web");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*quota*");
    }

    [Fact]
    public async Task Save_PartialNewProfile_IsRejected()
    {
        var act = () => _storage.SaveAsync(Input("previews", "https://s3.example", ""), "admin", "web");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DisableRole_LeavesVersionsReadableButNoVersionActive()
    {
        await _storage.SaveAsync(Input("documents", "https://s3.example", "documents"), "admin", "web");

        await _storage.DisableRoleAsync("documents", "admin", "web");

        (await _storage.GetAllAsync()).Where(x => x.Role == "documents")
            .Should().OnlyContain(x => !x.IsActive);
    }

    [Fact]
    public async Task Save_LegacyCredentialCorrection_RequiresConfirmation()
    {
        var legacy = Input("cloud-files-old", "https://s3.example", "cloud-files") with
        {
            IsLegacy = true,
            ConfirmLegacyMutation = true
        };
        var profile = await _storage.SaveAsync(legacy, "admin", "web");

        var act = () => _storage.SaveAsync(legacy with
        {
            ProfileId = profile.ProfileId,
            AccessKey = "corrected",
            ConfirmLegacyMutation = false
        }, "admin", "web");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*подтверждения*");
    }

    [Fact]
    public async Task Save_CredentialRotationAffectingLegacyProfile_RequiresLegacyConfirmation()
    {
        await _storage.SaveAsync(Input("images", "https://s3.example", "shared"), "admin", "web");
        await _storage.SaveAsync(Input("cloud-files-old", "https://s3.example", "shared") with
        {
            IsLegacy = true,
            ConfirmLegacyMutation = true
        }, "admin", "web");

        var act = () => _storage.SaveAsync(Input("images", "https://s3.example", "shared") with
        {
            AccessKey = "rotated",
            SecretKey = "rotated-secret"
        }, "admin", "web");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*legacy-профиль*подтверждения*");
    }

    private static StorageProfileInput Input(string role, string serviceUrl, string bucket, long quotaBytes = 0) => new(
        role,
        serviceUrl,
        "access",
        "secret",
        bucket,
        false,
        false,
        null,
        false,
        quotaBytes);

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }
}
