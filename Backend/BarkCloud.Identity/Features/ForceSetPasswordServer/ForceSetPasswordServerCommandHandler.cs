using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Identity.Persistence.Contexts;
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
    private readonly IdentityContext _context;
    private readonly MetricsCollector _metrics;

    public ForceSetPasswordServerCommandHandler(
        IPasswordsStorage passwordsStorage,
        INotificationOutbox notificationOutbox,
        ILogger<ForceSetPasswordServerCommandHandler> logger,
        IdentityContext context,
        MetricsCollector metrics)
    {
        _passwordsStorage = passwordsStorage;
        _notificationOutbox = notificationOutbox;
        _logger = logger;
        _context = context;
        _metrics = metrics;
    }

    public async Task<ForceSetPasswordServerResponse> Handle(ForceSetPasswordServerCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Принудительная смена пароля для пользователя {UserId} (admin)", request.UserId);

        var passwordHash = PasswordHasher.HashPassword(request.NewPassword);
        var notificationEnqueued = false;
        try
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                await _passwordsStorage.UpdateUserPasswordHash(request.UserId, passwordHash, cancellationToken);

                // Адрес и имя подберёт воркер outbox; нет адреса — письмо не отправляется.
                notificationEnqueued = await _notificationOutbox.EnqueueAsync(
                    request.UserId,
                    NotificationType.PasswordChangedByAdmin,
                    "Пароль изменён администратором",
                    new Dictionary<string, string>
                    {
                        { "adminusername", "AdminPanel" },
                        { "datetime", DateTime.UtcNow.ToString("dd.MM.yyyy HH:mm:ss") }
                    }, cancellationToken);

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
                throw;
            }
        }
        catch
        {
            _context.ChangeTracker.Clear();
            throw;
        }

        if (notificationEnqueued)
        {
            _metrics.Increment("notification_outbox_enqueued");
        }

        _logger.LogInformation("Пароль успешно изменён для пользователя {UserId} (admin)", request.UserId);

        return new ForceSetPasswordServerResponse();
    }
}
