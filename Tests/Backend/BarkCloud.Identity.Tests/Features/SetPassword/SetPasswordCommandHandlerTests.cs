using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Features.SetPassword;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Tests._Helpers;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Queue.Notifications;
using BarkCloud.TestKit;

using MassTransit;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Features.SetPassword;

public class SetPasswordCommandHandlerTests
{
    private readonly Mock<IPasswordsStorage> _passwords = new();
    private readonly Mock<IRefreshTokensStorage> _refreshTokens = new();
    private readonly Mock<PasswordChangedNotifier> _notifier;
    private readonly MetricsCollector _metrics = new();
    private readonly ILogger<SetPasswordCommandHandler> _logger = NullLogger<SetPasswordCommandHandler>.Instance;

    public SetPasswordCommandHandlerTests()
    {
        _notifier = new Mock<PasswordChangedNotifier>(
            Mock.Of<UsersServerApi.UsersServerApiClient>(),
            new Mock<NotificationQueueSender>(Mock.Of<IPublishEndpoint>(),
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()).Object,
            new Mock<LocationClient>(new HttpClient(), new MetricsCollector(), NullLogger<LocationClient>.Instance).Object,
            new RequestContext(),
            NullLogger<PasswordChangedNotifier>.Instance);
        _notifier.Setup(n => n.NotifyAsync(It.IsAny<long>())).Returns(Task.CompletedTask);
    }

    private SetPasswordCommandHandler CreateSut() => new(
        UserContextFactory.Create(42),
        _passwords.Object,
        _refreshTokens.Object,
        _notifier.Object,
        _metrics,
        _logger);

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
        _metrics.SnapshotAndReset().Should().ContainKey("password_changes");
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
}
