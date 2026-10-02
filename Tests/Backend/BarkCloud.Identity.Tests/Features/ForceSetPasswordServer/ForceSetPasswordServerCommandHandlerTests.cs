using BarkCloud.Identity.Features.ForceSetPasswordServer;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Shared.Queue.Notifications;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Features.ForceSetPasswordServer;

public class ForceSetPasswordServerCommandHandlerTests
{
    private readonly Mock<IPasswordsStorage> _passwords = new();
    private readonly Mock<INotificationOutbox> _outbox = new();

    private ForceSetPasswordServerCommandHandler CreateSut() => new(
        _passwords.Object,
        _outbox.Object,
        NullLogger<ForceSetPasswordServerCommandHandler>.Instance);

    [Fact]
    public async Task Handle_UpdatesHashAndQueuesAdminChangeMail()
    {
        await CreateSut().Handle(new ForceSetPasswordServerCommand { UserId = 7, NewPassword = "secret" }, default);

        _passwords.Verify(s => s.UpdateUserPasswordHash(7, It.Is<string>(h => h != "secret" && h.Length > 0)), Times.Once);
        _outbox.Verify(o => o.EnqueueAsync(
            7, NotificationType.PasswordChangedByAdmin, "Пароль изменён администратором",
            It.Is<Dictionary<string, string>>(p => p["adminusername"] == "AdminPanel" && p.ContainsKey("datetime")
                                                   && !p.ContainsKey("ip"))), Times.Once);
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
}
