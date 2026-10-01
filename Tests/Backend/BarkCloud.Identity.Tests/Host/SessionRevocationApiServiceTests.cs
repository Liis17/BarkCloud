using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Host;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Tests._Helpers;
using BarkCloud.Proto.SessionRevocation;
using BarkCloud.TestKit;

using Google.Protobuf.WellKnownTypes;

using Microsoft.Extensions.DependencyInjection;

using RevokedSession = BarkCloud.Identity.Domain.RevokedSession;

namespace BarkCloud.Identity.Tests.Host;

public class SessionRevocationApiServiceTests : IDisposable
{
    private readonly SqliteIdentityContext _db = new();
    private readonly ServiceProvider _services;
    private readonly SessionRevocationApiService _sut;
    private readonly DateTime _now = DateTime.UtcNow;

    public SessionRevocationApiServiceTests()
    {
        _services = new ServiceCollection()
            .AddScoped<IdentityContext>(_ => _db.CreateAdditionalContext())
            .BuildServiceProvider();
        _sut = new SessionRevocationApiService(new DbRevocationFeed(_services.GetRequiredService<IServiceScopeFactory>()));
    }

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
    }

    private async Task Seed()
    {
        _db.Context.RevokedSessions.AddRange(
            new RevokedSession { UserId = 1, DeviceId = "old", RevokedAt = _now.AddMinutes(-10), ExpiresAt = _now.AddHours(1) },
            new RevokedSession { UserId = 2, DeviceId = "boundary", RevokedAt = _now.AddMinutes(-1), ExpiresAt = _now.AddHours(1) },
            new RevokedSession { UserId = 3, DeviceId = "recent", RevokedAt = _now, ExpiresAt = _now.AddHours(1) },
            new RevokedSession { UserId = 4, DeviceId = "expired", RevokedAt = _now.AddMinutes(-1), ExpiresAt = _now.AddSeconds(-1) });
        await _db.Context.SaveChangesAsync();
    }

    [Fact]
    public async Task GetRevokedSessions_FullSnapshot_OnlyReturnsUnexpiredRecordsAndUtcServerTime()
    {
        await Seed();

        var response = await _sut.GetRevokedSessions(new GetRevokedSessionsRequest(), new TestServerCallContext());

        response.Sessions.Select(x => x.DeviceId).Should().BeEquivalentTo("old", "boundary", "recent");
        response.ServerTime.ToDateTime().Should().BeOnOrAfter(_now);
        var recent = response.Sessions.Single(x => x.UserId == 3);
        recent.RevokedAt.ToDateTime().Should().Be(_now);
        recent.ExpiresAt.ToDateTime().Should().Be(_now.AddHours(1));
    }

    [Fact]
    public async Task GetRevokedSessions_Delta_UsesInclusiveRevokedAtAndExcludesExpiredRecords()
    {
        await Seed();

        var response = await _sut.GetRevokedSessions(new GetRevokedSessionsRequest
        {
            ChangedSince = Timestamp.FromDateTime(_now.AddMinutes(-1))
        }, new TestServerCallContext());

        response.Sessions.Select(x => x.DeviceId).Should().BeEquivalentTo("boundary", "recent");
    }
}
