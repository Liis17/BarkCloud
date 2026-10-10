using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Features.SetPassword;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Tests._Helpers;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Queue.Notifications;
using BarkCloud.TestKit;

using MassTransit;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Features.SetPassword;

public class SetPasswordCommandHandlerTests : IDisposable
{
    private readonly Mock<IPasswordsStorage> _passwords = new();
    private readonly Mock<IAuthPropertiesStorage> _authProps = new();
    private readonly Mock<IRefreshTokensStorage> _refreshTokens = new();
    private readonly Mock<PasswordChangedNotifier> _notifier;
    private readonly MetricsCollector _metrics = new();
    private readonly ILogger<SetPasswordCommandHandler> _logger = NullLogger<SetPasswordCommandHandler>.Instance;
    private readonly SqliteIdentityContext _database = new();

    public void Dispose() => _database.Dispose();

    public SetPasswordCommandHandlerTests()
    {
        _notifier = new Mock<PasswordChangedNotifier>(Mock.Of<INotificationOutbox>(), new RequestContext());
        _notifier.Setup(n => n.NotifyAsync(It.IsAny<long>())).ReturnsAsync(true);
        _authProps.Setup(s => s.TryReserveReauthPasswordAttempt(42)).ReturnsAsync(true);
    }

    private SetPasswordCommandHandler CreateSut() => new(
        UserContextFactory.Create(42),
        _passwords.Object,
        _authProps.Object,
        _refreshTokens.Object,
        _notifier.Object,
        _metrics,
        _logger,
        _database.Context);

    [Fact]
    public async Task Handle_OldHashSetButOldPasswordEmpty_Throws()
    {
        _passwords.Setup(s => s.GetUserPasswordHash(42))
            .ReturnsAsync(PasswordHasher.HashPassword("oldp"));

        var act = () => CreateSut().Handle(new SetPasswordCommand { NewPassword = "new" }, default);

        await act.Should().ThrowAsync<InvalidOldPasswordException>();
    }

    [Fact]
    public async Task Handle_OldPasswordIncorrect_Throws()
    {
        _passwords.Setup(s => s.GetUserPasswordHash(42))
            .ReturnsAsync(PasswordHasher.HashPassword("oldp"));

        var act = () => CreateSut().Handle(
            new SetPasswordCommand { OldPassword = "wrong", NewPassword = "new" }, default);

        await act.Should().ThrowAsync<InvalidOldPasswordException>();
    }

    [Fact]
    public async Task Handle_NewPasswordSameAsOld_ThrowsAndDoesNotUpdateHash()
    {
        _passwords.Setup(s => s.GetUserPasswordHash(42))
            .ReturnsAsync(PasswordHasher.HashPassword("oldp"));

        var act = () => CreateSut().Handle(
            new SetPasswordCommand { OldPassword = "oldp", NewPassword = "oldp" }, default);

        await act.Should().ThrowAsync<NewPasswordSameAsOldException>();
        _passwords.Verify(s => s.UpdateUserPasswordHash(It.IsAny<long>(), It.IsAny<string>()), Times.Never);
        _notifier.Verify(n => n.NotifyAsync(It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public async Task Handle_ValidChange_UpdatesHashAndSendsNotification()
    {
        _passwords.Setup(s => s.GetUserPasswordHash(42))
            .ReturnsAsync(PasswordHasher.HashPassword("oldp"));
        _passwords.Setup(s => s.UpdateUserPasswordHash(42, It.IsAny<string>()))
            .ReturnsAsync(false);

        await CreateSut().Handle(new SetPasswordCommand { OldPassword = "oldp", NewPassword = "newp" }, default);

        _passwords.Verify(s => s.UpdateUserPasswordHash(42, It.IsAny<string>()), Times.Once);
        _notifier.Verify(n => n.NotifyAsync(42), Times.Once);
        _metrics.SnapshotAndReset().Should().ContainKey("password_changes")
            .And.ContainKey("notification_outbox_enqueued");
    }

    [Fact]
    public async Task Handle_OutboxStorageBroken_ThrowsAndRollsBack()
    {
        // F19: письмо ставится в очередь после записи пароля; сбой очереди не должен превращаться в ошибку клиенту.
        using var database = new SqliteIdentityContext();
        await database.Context.Database.ExecuteSqlRawAsync("DROP TABLE PendingNotifications");
        var outbox = new NotificationOutbox(
            database.Context, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), _metrics,
            NullLogger<NotificationOutbox>.Instance);
        var sut = new SetPasswordCommandHandler(
            UserContextFactory.Create(42), _passwords.Object, _authProps.Object, _refreshTokens.Object,
            new PasswordChangedNotifier(outbox, new RequestContext()), _metrics, _logger, database.Context);
        _passwords.Setup(s => s.GetUserPasswordHash(42)).ReturnsAsync(PasswordHasher.HashPassword("oldp"));
        _passwords.Setup(s => s.UpdateUserPasswordHash(42, It.IsAny<string>())).ReturnsAsync(false);

        var act = () => sut.Handle(new SetPasswordCommand { OldPassword = "oldp", NewPassword = "newp" }, default);

        await act.Should().ThrowAsync<DbUpdateException>();
        _passwords.Verify(s => s.UpdateUserPasswordHash(42, It.IsAny<string>()), Times.Once);
        _metrics.SnapshotAndReset().Should().ContainKey("notification_outbox_enqueue_failed");
    }

    [Fact]
    public async Task Handle_ValidChange_QueuesPasswordChangedMailForLaterDelivery()
    {
        using var database = new SqliteIdentityContext();
        var outbox = new NotificationOutbox(
            database.Context, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), _metrics,
            NullLogger<NotificationOutbox>.Instance);
        var sut = new SetPasswordCommandHandler(
            UserContextFactory.Create(42), _passwords.Object, _authProps.Object, _refreshTokens.Object,
            new PasswordChangedNotifier(outbox, new RequestContext()), _metrics, _logger, database.Context);
        _passwords.Setup(s => s.GetUserPasswordHash(42)).ReturnsAsync(PasswordHasher.HashPassword("oldp"));
        _passwords.Setup(s => s.UpdateUserPasswordHash(42, It.IsAny<string>())).ReturnsAsync(false);

        await sut.Handle(new SetPasswordCommand { OldPassword = "oldp", NewPassword = "newp" }, default);

        await using var reader = database.CreateAdditionalContext();
        var queued = await reader.PendingNotifications.SingleAsync();
        queued.UserId.Should().Be(42);
        queued.Type.Should().Be(NotificationType.PasswordChanged);
    }

    [Fact]
    public async Task Handle_InitialPasswordSet_DoesNotSendNotification()
    {
        _passwords.Setup(s => s.GetUserPasswordHash(42)).ReturnsAsync((string?)null);
        _passwords.Setup(s => s.UpdateUserPasswordHash(42, It.IsAny<string>()))
            .ReturnsAsync(true);

        await CreateSut().Handle(new SetPasswordCommand { NewPassword = "newp" }, default);

        _passwords.Verify(s => s.UpdateUserPasswordHash(42, It.IsAny<string>()), Times.Once);
        _notifier.Verify(n => n.NotifyAsync(It.IsAny<long>()), Times.Never);
        var snap = _metrics.SnapshotAndReset();
        snap.Should().ContainKey("password_changes");
        snap.Should().ContainKey("password_changes_initial");
    }

    [Fact]
    public async Task Handle_OldPasswordAttemptsExhausted_RejectsCorrectOldPasswordBeforeVerification()
    {
        _passwords.Setup(s => s.GetUserPasswordHash(42)).ReturnsAsync(PasswordHasher.HashPassword("oldp"));
        _authProps.Setup(s => s.TryReserveReauthPasswordAttempt(42)).ReturnsAsync(false);

        var act = () => CreateSut().Handle(
            new SetPasswordCommand { OldPassword = "oldp", NewPassword = "newp" }, default);

        await act.Should().ThrowAsync<PasswordAttemptsExceededException>();
        _passwords.Verify(s => s.UpdateUserPasswordHash(It.IsAny<long>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Handle_EmptyOldPassword_DoesNotSpendAttempt()
    {
        _passwords.Setup(s => s.GetUserPasswordHash(42)).ReturnsAsync(PasswordHasher.HashPassword("oldp"));

        var act = () => CreateSut().Handle(new SetPasswordCommand { NewPassword = "newp" }, default);

        await act.Should().ThrowAsync<InvalidOldPasswordException>();
        _authProps.Verify(s => s.TryReserveReauthPasswordAttempt(It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WrongOldPassword_SpendsAttemptAndKeepsCounter()
    {
        _passwords.Setup(s => s.GetUserPasswordHash(42)).ReturnsAsync(PasswordHasher.HashPassword("oldp"));

        var act = () => CreateSut().Handle(
            new SetPasswordCommand { OldPassword = "wrong", NewPassword = "newp" }, default);

        await act.Should().ThrowAsync<InvalidOldPasswordException>();
        _authProps.Verify(s => s.TryReserveReauthPasswordAttempt(42), Times.Once);
        _authProps.Verify(s => s.ResetReauthPasswordAttempts(It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public async Task Handle_CorrectOldPassword_ResetsAttempts()
    {
        _passwords.Setup(s => s.GetUserPasswordHash(42)).ReturnsAsync(PasswordHasher.HashPassword("oldp"));
        _passwords.Setup(s => s.UpdateUserPasswordHash(42, It.IsAny<string>())).ReturnsAsync(false);

        await CreateSut().Handle(new SetPasswordCommand { OldPassword = "oldp", NewPassword = "newp" }, default);

        _authProps.Verify(s => s.ResetReauthPasswordAttempts(42), Times.Once);
    }

    [Fact]
    public async Task Handle_InitialPasswordSet_DoesNotTouchAttemptCounter()
    {
        _passwords.Setup(s => s.GetUserPasswordHash(42)).ReturnsAsync((string?)null);
        _passwords.Setup(s => s.UpdateUserPasswordHash(42, It.IsAny<string>())).ReturnsAsync(true);

        await CreateSut().Handle(new SetPasswordCommand { NewPassword = "newp" }, default);

        _authProps.Verify(s => s.TryReserveReauthPasswordAttempt(It.IsAny<long>()), Times.Never);
    }
}
