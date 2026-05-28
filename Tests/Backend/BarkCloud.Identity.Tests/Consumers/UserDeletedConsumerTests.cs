using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Identity.Consumers;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Settings;
using BarkCloud.Shared.Queue.Identity;
using BarkCloud.Shared.Queue.Users;

using MassTransit;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Consumers;

public class UserDeletedConsumerTests
{
    private readonly Mock<IRefreshTokensStorage> _refreshTokens = new();
    private readonly Mock<IPasswordsStorage> _passwords = new();
    private readonly Mock<IAuthPropertiesStorage> _authProps = new();
    private readonly Mock<IResetPasswordsStorage> _resets = new();
    private readonly Mock<IConfirmationCodesStorage> _codes = new();
    private readonly Mock<IPublishEndpoint> _publish = new();
    private readonly JwtSettings _jwt = new() { SecretKey = "k", Issuer = "i", Audience = "a", ExpiryMinutes = 15 };
    private readonly MetricsCollector _metrics = new();

    private UserDeletedConsumer CreateSut() => new(
        _refreshTokens.Object, _passwords.Object, _authProps.Object,
        _resets.Object, _codes.Object, _publish.Object, _jwt, _metrics,
        NullLogger<UserDeletedConsumer>.Instance);

    [Fact]
    public async Task Consume_NoDevices_StillCleansUserData()
    {
        _refreshTokens.Setup(s => s.DeleteAllByUserId(42)).ReturnsAsync(new List<string>());
        var msg = new UserDeleted { UserId = 42 };
        var ctx = new Mock<ConsumeContext<UserDeleted>>();
        ctx.SetupGet(c => c.Message).Returns(msg);

        await CreateSut().Consume(ctx.Object);

        _publish.Verify(p => p.Publish(It.IsAny<SessionRevokedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        _passwords.Verify(s => s.DeleteByUserId(42), Times.Once);
        _authProps.Verify(s => s.DeleteByUserId(42), Times.Once);
        _resets.Verify(s => s.DeleteByUserId(42), Times.Once);
        _codes.Verify(s => s.DeleteByOwnerId(42), Times.Once);
    }

    [Fact]
    public async Task Consume_WithDevices_PublishesSessionRevokedForEach()
    {
        _refreshTokens.Setup(s => s.DeleteAllByUserId(42))
            .ReturnsAsync(new List<string> { "d1", "d2", "d3" });
        var msg = new UserDeleted { UserId = 42 };
        var ctx = new Mock<ConsumeContext<UserDeleted>>();
        ctx.SetupGet(c => c.Message).Returns(msg);

        await CreateSut().Consume(ctx.Object);

        _publish.Verify(p => p.Publish(
            It.Is<SessionRevokedEvent>(e => e.UserId == 42),
            It.IsAny<CancellationToken>()), Times.Exactly(3));
        _metrics.SnapshotAndReset().Should().ContainKey("accounts_cleaned_identity");
    }
}
