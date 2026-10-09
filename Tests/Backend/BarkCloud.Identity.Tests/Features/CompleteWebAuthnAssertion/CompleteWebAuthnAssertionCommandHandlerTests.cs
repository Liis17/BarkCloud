using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Features.CompleteWebAuthnAssertion;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Queue.Notifications;
using BarkCloud.Shared.Exceptions.Identity;

using Fido2NetLib;
using Fido2NetLib.Objects;

using MediatR;

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

    [Fact]
    public async Task Handle_ValidAssertion_ForwardsCancellationTokenToSessionIssuer()
    {
        using var cts = new CancellationTokenSource();
        var challengeId = Guid.NewGuid();
        var credentialId = new byte[] { 1, 2, 3 };
        var storage = new Mock<IWebAuthnStorage>();
        storage.Setup(s => s.GetChallenge(challengeId)).ReturnsAsync(new WebAuthnChallenge
        {
            Id = challengeId,
            Type = WebAuthnChallengeType.Assertion,
            OptionsJson = new AssertionOptions
            {
                Challenge = [4, 5, 6],
                RpId = "example.test",
                AllowCredentials = [],
                UserVerification = UserVerificationRequirement.Required
            }.ToJson(),
            ExpiresAt = DateTime.UtcNow.AddMinutes(1)
        });
        storage.Setup(s => s.GetCredentialByCredentialId(It.Is<byte[]>(id => id.SequenceEqual(credentialId))))
            .ReturnsAsync(new WebAuthnCredential
            {
                Id = 9,
                UserId = 7,
                CredentialId = credentialId,
                PublicKey = [7, 8, 9],
                SignatureCounter = 4
            });
        storage.Setup(s => s.UpdateCounter(9, 5)).Returns(Task.CompletedTask);
        storage.Setup(s => s.DeleteChallenge(challengeId)).Returns(Task.CompletedTask);

        var fido2 = new Mock<IFido2>();
        fido2.Setup(f => f.MakeAssertionAsync(It.IsAny<MakeAssertionParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VerifyAssertionResult { SignCount = 5 });

        var metrics = new MetricsCollector();
        var sessionIssuer = new Mock<SessionIssuer>(
            Mock.Of<UsersServerApi.UsersServerApiClient>(), Mock.Of<IMediator>(), Mock.Of<INotificationOutbox>(),
            Mock.Of<IRefreshTokensStorage>(), new RequestContext(),
            new Mock<LocationClient>(new HttpClient(), metrics, NullLogger<LocationClient>.Instance).Object,
            metrics, NullLogger<SessionIssuer>.Instance);
        sessionIssuer.Setup(s => s.IssueAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(new AuthResponse
        {
            AccessToken = new Token { Value = "access" },
            RefreshToken = new Token { Value = "refresh" }
        });
        var limiter = new Mock<IAuthRateLimiter>();
        limiter.Setup(l => l.EnsureSourceAsync(AuthLimits.WebAuthnByIp)).Returns(Task.CompletedTask);
        var sut = new CompleteWebAuthnAssertionCommandHandler(
            storage.Object, fido2.Object, sessionIssuer.Object, limiter.Object, metrics,
            NullLogger<CompleteWebAuthnAssertionCommandHandler>.Instance);

        var response = await sut.Handle(new CompleteWebAuthnAssertionCommand
        {
            ChallengeId = challengeId.ToString(),
            AssertionJson = "{\"id\":\"AQID\",\"rawId\":\"AQID\",\"type\":\"public-key\",\"response\":{\"authenticatorData\":\"AQID\",\"clientDataJSON\":\"AQID\",\"signature\":\"AQID\",\"userHandle\":\"AQID\"}}"
        }, cts.Token).WaitAsync(TimeSpan.FromSeconds(2));

        response.AccessToken.Value.Should().Be("access");
        response.RefreshToken.Value.Should().Be("refresh");
        sessionIssuer.Verify(s => s.IssueAsync(7, cts.Token), Times.Once);
        storage.Verify(s => s.UpdateCounter(9, 5), Times.Once);
        storage.Verify(s => s.DeleteChallenge(challengeId), Times.Once);
        metrics.SnapshotAndReset()["webauthn_login_success"].Should().Be(1);
    }
}
