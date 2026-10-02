using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Identity.Features.CompleteWebAuthnAssertion;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Shared.Exceptions.Identity;

using Fido2NetLib;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Features.CompleteWebAuthnAssertion;

public class CompleteWebAuthnAssertionCommandHandlerTests
{
    [Fact]
    public async Task Handle_SourceLimitExceeded_ThrowsBeforeChallengeLookup()
    {
        var storage = new Mock<IWebAuthnStorage>();
        var limiter = new Mock<IAuthRateLimiter>();
        limiter.Setup(l => l.EnsureSourceAsync(AuthLimits.WebAuthnByIp)).ThrowsAsync(new TooManyRequestsException());
        var sut = new CompleteWebAuthnAssertionCommandHandler(
            storage.Object, Mock.Of<IFido2>(), null!, limiter.Object, new MetricsCollector(),
            NullLogger<CompleteWebAuthnAssertionCommandHandler>.Instance);

        var act = () => sut.Handle(
            new CompleteWebAuthnAssertionCommand { ChallengeId = Guid.NewGuid().ToString(), AssertionJson = "{}" }, default);

        await act.Should().ThrowAsync<TooManyRequestsException>();
        storage.Verify(s => s.GetChallenge(It.IsAny<Guid>()), Times.Never);
    }
}
