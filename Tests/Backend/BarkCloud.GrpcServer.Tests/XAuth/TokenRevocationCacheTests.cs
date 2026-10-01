using BarkCloud.GrpcServer.XAuth;

namespace BarkCloud.GrpcServer.Tests.XAuth;

public class TokenRevocationCacheTests
{
    [Fact]
    public void IsRevoked_NotRevoked_ReturnsFalse()
    {
        var sut = new TokenRevocationCache();

        sut.IsRevoked(userId: 1, deviceId: "d1", tokenIssuedAt: DateTime.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void Revoke_ThenIsRevoked_ReturnsTrue()
    {
        var sut = new TokenRevocationCache();

        sut.Revoke(userId: 1, deviceId: "d1", revokedAt: DateTime.UtcNow, expiresAt: DateTime.UtcNow.AddHours(1));

        sut.IsRevoked(1, "d1", DateTime.UtcNow.AddMinutes(-1)).Should().BeTrue();
    }

    [Fact]
    public void Revoke_KeyedByUserAndDevice_DoesNotAffectOtherDevices()
    {
        var sut = new TokenRevocationCache();

        sut.Revoke(1, "d1", DateTime.UtcNow, DateTime.UtcNow.AddHours(1));

        sut.IsRevoked(1, "d2", DateTime.UtcNow).Should().BeFalse();
        sut.IsRevoked(2, "d1", DateTime.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void Cleanup_RemovesExpiredEntries()
    {
        var sut = new TokenRevocationCache();
        sut.Revoke(1, "expired", DateTime.UtcNow, DateTime.UtcNow.AddSeconds(-1));
        sut.Revoke(2, "live", DateTime.UtcNow, DateTime.UtcNow.AddHours(1));

        sut.Cleanup();

        sut.IsRevoked(1, "expired", DateTime.UtcNow.AddMinutes(-1)).Should().BeFalse();
        sut.IsRevoked(2, "live", DateTime.UtcNow.AddMinutes(-1)).Should().BeTrue();
    }

    [Fact]
    public void Cleanup_NoExpiredEntries_KeepsAll()
    {
        var sut = new TokenRevocationCache();
        sut.Revoke(1, "a", DateTime.UtcNow, DateTime.UtcNow.AddHours(1));
        sut.Revoke(2, "b", DateTime.UtcNow, DateTime.UtcNow.AddHours(2));

        sut.Cleanup();

        sut.IsRevoked(1, "a", DateTime.UtcNow.AddMinutes(-1)).Should().BeTrue();
        sut.IsRevoked(2, "b", DateTime.UtcNow.AddMinutes(-1)).Should().BeTrue();
    }

    [Fact]
    public void Revoke_OutOfOrderAndDuplicateRecords_KeepsMaximumRevokedAtAndExpiry()
    {
        var sut = new TokenRevocationCache();
        var now = DateTime.UtcNow;
        sut.Revoke(1, "d1", now, now.AddHours(2));
        sut.Revoke(1, "d1", now.AddMinutes(-10), now.AddHours(1));
        sut.Revoke(1, "d1", now, now.AddHours(2));

        sut.IsRevoked(1, "d1", now.AddMinutes(-5)).Should().BeTrue();
        sut.IsRevoked(1, "d1", now.AddSeconds(1)).Should().BeFalse();
    }

    [Fact]
    public void Revoke_OlderRecordWithLongerExpiry_DoesNotShortenLifetime()
    {
        var sut = new TokenRevocationCache();
        var now = DateTime.UtcNow;
        sut.Revoke(1, "d1", now.AddMinutes(-2), now.AddHours(1));
        sut.Revoke(1, "d1", now.AddMinutes(-1), now.AddSeconds(-1));

        sut.Cleanup();

        sut.IsRevoked(1, "d1", now.AddMinutes(-1)).Should().BeTrue();
    }

    [Fact]
    public void Revoke_SameSecondLogin_RemainsRevokedBecauseJwtIatHasSecondPrecision()
    {
        var sut = new TokenRevocationCache();
        var second = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).UtcDateTime;
        sut.Revoke(1, "d1", second.AddMilliseconds(100), second.AddHours(1));

        // Вход в second + 900 мс записывает в JWT тот же iat = second.
        sut.IsRevoked(1, "d1", second).Should().BeTrue();
        sut.IsRevoked(1, "d1", second.AddSeconds(1)).Should().BeFalse();
    }
}
