using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Identity.Features.ForceSetPasswordServer;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Tests._Helpers;
using BarkCloud.Shared.Queue.Notifications;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Features.ForceSetPasswordServer;

public class ForceSetPasswordServerCommandHandlerTests : IDisposable
{
    private readonly Mock<IPasswordsStorage> _passwords = new();
    private readonly Mock<IWebAuthnStorage> _webAuthn = new();
    private readonly Mock<IRefreshTokensStorage> _refreshTokens = new();
    private readonly Mock<INotificationOutbox> _outbox = new();
    private readonly SqliteIdentityContext _database = new();

    public ForceSetPasswordServerCommandHandlerTests()
    {
        _outbox.Setup(o => o.EnqueueAsync(It.IsAny<long>(), It.IsAny<NotificationType>(), It.IsAny<string>(),
            It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _webAuthn.Setup(s => s.RemoveAllCredentials(It.IsAny<long>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
        _refreshTokens.Setup(s => s.RevokeAllSessions(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
    }

    public void Dispose() => _database.Dispose();

    private ForceSetPasswordServerCommandHandler CreateSut() => new(
        _passwords.Object,
        _webAuthn.Object,
        _refreshTokens.Object,
        _outbox.Object,
        NullLogger<ForceSetPasswordServerCommandHandler>.Instance,
        _database.Context,
        new MetricsCollector());

    [Fact]
    public async Task Handle_UpdatesHashAndQueuesAdminChangeMail()
    {
        await CreateSut().Handle(new ForceSetPasswordServerCommand { UserId = 7, NewPassword = "secret" }, default);

        _passwords.Verify(s => s.UpdateUserPasswordHash(7, It.Is<string>(h => h != "secret" && h.Length > 0)), Times.Once);
        _outbox.Verify(o => o.EnqueueAsync(
            7, NotificationType.PasswordChangedByAdmin, "Пароль изменён администратором",
            It.Is<Dictionary<string, string>>(p => p["adminusername"] == "AdminPanel" && p.ContainsKey("datetime")
                                                   && !p.ContainsKey("ip"))), Times.Once);
        _webAuthn.Verify(s => s.RemoveAllCredentials(7, It.IsAny<CancellationToken>()), Times.Once);
        _refreshTokens.Verify(s => s.RevokeAllSessions(7, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_PasswordNotSaved_QueuesNothing()
    {
        _passwords.Setup(s => s.UpdateUserPasswordHash(7, It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("db down"));

        var act = () => CreateSut().Handle(new ForceSetPasswordServerCommand { UserId = 7, NewPassword = "secret" }, default);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _outbox.Verify(o => o.EnqueueAsync(
            It.IsAny<long>(), It.IsAny<NotificationType>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()), Times.Never);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Handle_AppliesBothResetChoices(bool resetPasskeys, bool revokeSessions)
    {
        await CreateSut().Handle(new ForceSetPasswordServerCommand
        {
            UserId = 7,
            NewPassword = "secret",
            ResetPasskeys = resetPasskeys,
            RevokeOtherSessions = revokeSessions
        }, default);

        _webAuthn.Verify(s => s.RemoveAllCredentials(7, It.IsAny<CancellationToken>()),
            resetPasskeys ? Times.Once : Times.Never);
        _refreshTokens.Verify(s => s.RevokeAllSessions(7, null, It.IsAny<CancellationToken>()),
            revokeSessions ? Times.Once : Times.Never);
    }
}
