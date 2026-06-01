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

        sut.Revoke(userId: 1, deviceId: "d1", accessTokenExpiresAt: DateTime.UtcNow.AddHours(1));

        sut.IsRevoked(1, "d1", DateTime.UtcNow.AddMinutes(-1)).Should().BeTrue();
    }

    [Fact]
    public void Revoke_KeyedByUserAndDevice_DoesNotAffectOtherDevices()
    {
        var sut = new TokenRevocationCache();

        sut.Revoke(1, "d1", DateTime.UtcNow.AddHours(1));

        sut.IsRevoked(1, "d2", DateTime.UtcNow).Should().BeFalse();
        sut.IsRevoked(2, "d1", DateTime.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void Cleanup_RemovesExpiredEntries()
    {
        var sut = new TokenRevocationCache();
        sut.Revoke(1, "expired", DateTime.UtcNow.AddSeconds(-1));
        sut.Revoke(2, "live", DateTime.UtcNow.AddHours(1));

        sut.Cleanup();

        sut.IsRevoked(1, "expired", DateTime.UtcNow.AddMinutes(-1)).Should().BeFalse();
        sut.IsRevoked(2, "live", DateTime.UtcNow.AddMinutes(-1)).Should().BeTrue();
    }

    [Fact]
    public void Cleanup_NoExpiredEntries_KeepsAll()
    {
        var sut = new TokenRevocationCache();
        sut.Revoke(1, "a", DateTime.UtcNow.AddHours(1));
        sut.Revoke(2, "b", DateTime.UtcNow.AddHours(2));

        sut.Cleanup();

        sut.IsRevoked(1, "a", DateTime.UtcNow.AddMinutes(-1)).Should().BeTrue();
        sut.IsRevoked(2, "b", DateTime.UtcNow.AddMinutes(-1)).Should().BeTrue();
    }
}
