using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Persistence.Exceptions;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Settings;
using BarkCloud.Identity.Tests._Helpers;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.Tests.Persistence;

public class RefreshTokensStorageRevocationTests : IDisposable
{
    private readonly SqliteIdentityContext _db = new();
    private readonly JwtSettings _settings = new() { ExpiryMinutes = 60 };

    private RefreshTokensStorage CreateSut() => new(_db.Context, _settings);

    public void Dispose() => _db.Dispose();

    private async Task Seed(params (long UserId, string DeviceId)[] sessions)
    {
        foreach (var (userId, deviceId) in sessions)
        {
            await CreateSut().CreateNewRefreshToken(Guid.NewGuid().ToString(), userId, deviceId, 1);
        }
        _db.Context.ChangeTracker.Clear();
    }

    [Fact]
    public async Task RevokeDevice_DeletesOnlyItsRefreshTokensAndCreatesOneDurableRevocation()
    {
        await Seed((42, "d1"), (42, "d1"), (42, "d2"), (43, "d1"));
        var maxId = await _db.Context.RefreshTokens.Where(x => x.UserId == 42 && x.DeviceId == "d1").MaxAsync(x => x.Id);
        _db.Context.ChangeTracker.Clear();
        var before = DateTime.UtcNow;

        await CreateSut().RevokeSession("d1", 42);

        using var persisted = _db.CreateAdditionalContext();
        (await persisted.RefreshTokens.CountAsync()).Should().Be(2);
        (await persisted.RefreshTokens.AnyAsync(x => x.UserId == 42 && x.DeviceId == "d1"))
            .Should().BeFalse();
        var revocation = await persisted.RevokedSessions.SingleAsync();
        revocation.UserId.Should().Be(42);
        revocation.DeviceId.Should().Be("d1");
        revocation.MaxSessionId.Should().Be(maxId);
        revocation.RevokedAt.Should().BeOnOrAfter(before);
        revocation.ExpiresAt.Should().Be(revocation.RevokedAt.AddMinutes(61));
    }

    [Fact]
    public async Task RevokeDevice_MissingRefresh_ThrowsWithoutRevocation()
    {
        var action = () => CreateSut().RevokeSession("missing", 42);

        await action.Should().ThrowAsync<RefreshTokenNotFoundException>();
        (await _db.Context.RevokedSessions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RevokeDeviceSafe_MissingRefresh_StillRevokesAccessToken()
    {
        await CreateSut().RevokeSessionSafe("d1", 42);

        using var persisted = _db.CreateAdditionalContext();
        var revocation = await persisted.RevokedSessions.SingleAsync();
        revocation.DeviceId.Should().Be("d1");
        revocation.MaxSessionId.Should().BeNull("у устройства нет refresh — порога сессии нет, отзыв по времени");
    }

    [Fact]
    public async Task RevokeAll_EveryDeviceIsRevokedBySessionThreshold()
    {
        await Seed((42, "current"), (42, "current"), (42, "other"), (42, "other"), (42, "third"), (43, "other"));
        var maxIds = (await _db.Context.RefreshTokens.Where(x => x.UserId == 42).ToListAsync())
            .GroupBy(x => x.DeviceId).ToDictionary(x => x.Key, x => x.Max(t => t.Id));
        _db.Context.ChangeTracker.Clear();

        var count = await CreateSut().RevokeAllSessions(42, "current");

        // Число прочих устройств; текущее считается отдельно — его сессия заменяется новой.
        count.Should().Be(2);
        using var persisted = _db.CreateAdditionalContext();
        (await persisted.RefreshTokens.SingleAsync()).UserId.Should().Be(43);
        var revoked = await persisted.RevokedSessions.ToListAsync();
        revoked.Select(x => x.DeviceId).Should().BeEquivalentTo("other", "third", "current");
        revoked.Should().AllSatisfy(x => x.MaxSessionId.Should().Be(maxIds[x.DeviceId]));
        revoked.Select(x => x.RevokedAt).Distinct().Should().ContainSingle();
        revoked.Select(x => x.ExpiresAt).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task RevokeAll_CurrentDeviceWithoutRefreshTokens_CreatesNoRecordForIt()
    {
        await Seed((42, "other"));

        await CreateSut().RevokeAllSessions(42, "current");

        using var persisted = _db.CreateAdditionalContext();
        (await persisted.RevokedSessions.SingleAsync()).DeviceId.Should().Be("other");
    }

    [Fact]
    public async Task RevokeAll_Redelivery_PreservesOriginalRevocations()
    {
        await Seed((42, "d1"), (42, "d2"));
        (await CreateSut().RevokeAllSessions(42)).Should().Be(2);
        using var persisted = _db.CreateAdditionalContext();
        var original = await persisted.RevokedSessions.AsNoTracking().ToListAsync();

        (await CreateSut().RevokeAllSessions(42)).Should().Be(0);

        (await persisted.RevokedSessions.AsNoTracking().ToListAsync()).Should().BeEquivalentTo(original);
    }

    [Fact]
    public async Task Revoke_CleansExpiredRecordsAndKeepsActiveRecords()
    {
        _db.Context.RevokedSessions.AddRange(
            new RevokedSession { UserId = 1, DeviceId = "expired", RevokedAt = DateTime.UtcNow.AddHours(-2), ExpiresAt = DateTime.UtcNow.AddMinutes(-1) },
            new RevokedSession { UserId = 1, DeviceId = "active", RevokedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddHours(1) });
        await _db.Context.SaveChangesAsync();

        await CreateSut().RevokeSessionSafe("new", 42);

        using var persisted = _db.CreateAdditionalContext();
        (await persisted.RevokedSessions.Select(x => x.DeviceId).ToListAsync())
            .Should().BeEquivalentTo("active", "new");
    }

    [Fact]
    public async Task DeleteDeviceSafe_ForNewLogin_DoesNotCreateRevocation()
    {
        await Seed((42, "d1"));

        await CreateSut().DeleteRefreshTokensByDeviceIdSafe("d1", 42);

        using var persisted = _db.CreateAdditionalContext();
        (await persisted.RefreshTokens.CountAsync()).Should().Be(0);
        (await persisted.RevokedSessions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Revoke_WhenRevocationInsertFails_RefreshDeletionRollsBack()
    {
        await Seed((42, "d1"));
        await _db.Context.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER reject_revocation BEFORE INSERT ON RevokedSessions
            BEGIN SELECT RAISE(ABORT, 'revocation write failed'); END;
            """);

        var action = () => CreateSut().RevokeSession("d1", 42);
        await action.Should().ThrowAsync<DbUpdateException>();

        using var persisted = _db.CreateAdditionalContext();
        (await persisted.RefreshTokens.CountAsync()).Should().Be(1);
        (await persisted.RevokedSessions.CountAsync()).Should().Be(0);
    }
}
