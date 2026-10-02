using BarkCloud.Identity.Features.BeginWebAuthnAssertion;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Shared.Exceptions.Identity;

using Fido2NetLib;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Features.BeginWebAuthnAssertion;

public class BeginWebAuthnAssertionCommandHandlerTests
{
    [Fact]
    public async Task Handle_SourceLimitExceeded_ThrowsBeforeWritingChallenge()
    {
        var storage = new Mock<IWebAuthnStorage>();
        var fido2 = new Mock<IFido2>();
        var limiter = new Mock<IAuthRateLimiter>();
        limiter.Setup(l => l.EnsureSourceAsync(AuthLimits.WebAuthnByIp)).ThrowsAsync(new TooManyRequestsException());
        var sut = new BeginWebAuthnAssertionCommandHandler(
            storage.Object, fido2.Object, limiter.Object, NullLogger<BeginWebAuthnAssertionCommandHandler>.Instance);

        var act = () => sut.Handle(new BeginWebAuthnAssertionCommand(), default);

        await act.Should().ThrowAsync<TooManyRequestsException>();
        storage.Verify(s => s.SaveChallenge(It.IsAny<BarkCloud.Identity.Domain.WebAuthnChallenge>()), Times.Never);
        fido2.VerifyNoOtherCalls();
    }
}
