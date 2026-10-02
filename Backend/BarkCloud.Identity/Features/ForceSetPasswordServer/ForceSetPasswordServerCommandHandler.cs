using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Shared.Queue.Notifications;

using MediatR;

namespace BarkCloud.Identity.Features.ForceSetPasswordServer;

public class ForceSetPasswordServerCommandHandler : IRequestHandler<ForceSetPasswordServerCommand, ForceSetPasswordServerResponse>
{
    private readonly IPasswordsStorage _passwordsStorage;
    private readonly INotificationOutbox _notificationOutbox;
    private readonly ILogger<ForceSetPasswordServerCommandHandler> _logger;

    public ForceSetPasswordServerCommandHandler(
        IPasswordsStorage passwordsStorage,
        INotificationOutbox notificationOutbox,
        ILogger<ForceSetPasswordServerCommandHandler> logger)
    {
        _passwordsStorage = passwordsStorage;
        _notificationOutbox = notificationOutbox;
        _logger = logger;
    }

    public async Task<ForceSetPasswordServerResponse> Handle(ForceSetPasswordServerCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Принудительная смена пароля для пользователя {UserId} (admin)", request.UserId);

        var passwordHash = PasswordHasher.HashPassword(request.NewPassword);
        await _passwordsStorage.UpdateUserPasswordHash(request.UserId, passwordHash);

        _logger.LogInformation("Пароль успешно изменён для пользователя {UserId} (admin)", request.UserId);

        // Адрес и имя подберёт воркер outbox; нет адреса — письмо не отправляется.
        await _notificationOutbox.EnqueueAsync(
            request.UserId,
            NotificationType.PasswordChangedByAdmin,
            "Пароль изменён администратором",
            new Dictionary<string, string>
            {
                { "adminusername", "AdminPanel" },
                { "datetime", DateTime.UtcNow.ToString("dd.MM.yyyy HH:mm:ss") }
            });

        return new ForceSetPasswordServerResponse();
    }
}
