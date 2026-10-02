using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Shared.Exceptions.Identity;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Services;

public class AuthRateLimiterTests
{
    private static readonly AuthLimits.Policy Policy = new("test", 3, TimeSpan.FromMinutes(10));

    private readonly Mock<IAttemptCountersStorage> _storage = new();

    private AuthRateLimiter CreateSut(string? sourceIp = "203.0.113.7")
        => new(_storage.Object, new RequestContext { SourceIp = sourceIp }, NullLogger<AuthRateLimiter>.Instance);

    [Fact]
    public async Task EnsureSource_Allowed_UsesScopedIpKey()
    {
        _storage.Setup(x => x.TryReserve("test:ip:203.0.113.7", 3, TimeSpan.FromMinutes(10)))
            .ReturnsAsync(new AttemptReservation(true, TimeSpan.Zero));

        await CreateSut().EnsureSourceAsync(Policy);

        _storage.VerifyAll();
    }

    [Fact]
    public async Task EnsureSource_Exhausted_ThrowsTooManyRequestsWithRetryAfter()
    {
        _storage.Setup(x => x.TryReserve(It.IsAny<string>(), 3, It.IsAny<TimeSpan>()))
            .ReturnsAsync(new AttemptReservation(false, TimeSpan.FromSeconds(90)));

        var act = () => CreateSut().EnsureSourceAsync(Policy);

        var ex = (await act.Should().ThrowAsync<TooManyRequestsException>()).Which;
        ex.ErrorMetadata["x-retry-after-seconds"].Should().Be("90");
    }

    [Fact]
    public async Task EnsureSource_UnknownSource_SkipsLimit()
    {
        await CreateSut(sourceIp: null).EnsureSourceAsync(Policy);

        _storage.Verify(x => x.TryReserve(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<TimeSpan>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TryReserve_ReturnsStorageDecision_ForScopedSubjectKey(bool allowed)
    {
        _storage.Setup(x => x.TryReserve("test:42", 3, TimeSpan.FromMinutes(10)))
            .ReturnsAsync(new AttemptReservation(allowed, TimeSpan.Zero));

        (await CreateSut().TryReserveAsync(Policy, "42")).Should().Be(allowed);
    }

    [Fact]
    public async Task TryReserve_DoesNotDependOnSourceIp()
    {
        _storage.Setup(x => x.TryReserve("test:42", 3, It.IsAny<TimeSpan>()))
            .ReturnsAsync(new AttemptReservation(true, TimeSpan.Zero));

        await CreateSut("203.0.113.7").TryReserveAsync(Policy, "42");
        await CreateSut("198.51.100.9").TryReserveAsync(Policy, "42");

        _storage.Verify(x => x.TryReserve("test:42", 3, It.IsAny<TimeSpan>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Reset_ClearsScopedSubjectKey()
    {
        await CreateSut().ResetAsync(Policy, "42");

        _storage.Verify(x => x.Reset("test:42"), Times.Once);
    }

    [Fact]
    public void TooManyRequests_WithoutRetryAfter_HasNoMetadata()
        => new TooManyRequestsException().ErrorMetadata.Should().BeEmpty();
}
