using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Identity.Consumers;
using BarkCloud.Identity.Persistence.Services;
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
    private readonly MetricsCollector _metrics = new();

    private UserDeletedConsumer CreateSut() => new(
        _refreshTokens.Object, _passwords.Object, _authProps.Object,
        _resets.Object, _codes.Object, _metrics,
        NullLogger<UserDeletedConsumer>.Instance);

    [Fact]
    public async Task Consume_NoDevices_StillCleansUserData()
    {
        _refreshTokens.Setup(s => s.RevokeAllSessions(42, null, default)).ReturnsAsync(0);
        var msg = new UserDeleted { UserId = 42 };
        var ctx = new Mock<ConsumeContext<UserDeleted>>();
        ctx.SetupGet(c => c.Message).Returns(msg);

        await CreateSut().Consume(ctx.Object);

        _refreshTokens.Verify(s => s.RevokeAllSessions(42, null, default), Times.Once);
        _passwords.Verify(s => s.DeleteByUserId(42), Times.Once);
        _authProps.Verify(s => s.DeleteByUserId(42), Times.Once);
        _resets.Verify(s => s.DeleteByUserId(42), Times.Once);
        _codes.Verify(s => s.DeleteByOwnerId(42), Times.Once);
    }

    [Fact]
    public async Task Consume_WithDevices_RevokesAllSessions()
    {
        _refreshTokens.Setup(s => s.RevokeAllSessions(42, null, default))
            .ReturnsAsync(3);
        var msg = new UserDeleted { UserId = 42 };
        var ctx = new Mock<ConsumeContext<UserDeleted>>();
        ctx.SetupGet(c => c.Message).Returns(msg);

        await CreateSut().Consume(ctx.Object);

        _refreshTokens.Verify(s => s.RevokeAllSessions(42, null, default), Times.Once);
        _metrics.SnapshotAndReset().Should().ContainKey("accounts_cleaned_identity");
    }

    [Fact]
    public async Task Consume_Redelivery_RepeatsCleanupSafelyWhenRefreshTokensAreAlreadyGone()
    {
        _refreshTokens.SetupSequence(s => s.RevokeAllSessions(42, null, default))
            .ReturnsAsync(2).ReturnsAsync(0);
        var context = new Mock<ConsumeContext<UserDeleted>>();
        context.SetupGet(c => c.Message).Returns(new UserDeleted { UserId = 42 });
        var sut = CreateSut();

        await sut.Consume(context.Object);
        await sut.Consume(context.Object);

        _refreshTokens.Verify(s => s.RevokeAllSessions(42, null, default), Times.Exactly(2));
        _passwords.Verify(s => s.DeleteByUserId(42), Times.Exactly(2));
    }
}
