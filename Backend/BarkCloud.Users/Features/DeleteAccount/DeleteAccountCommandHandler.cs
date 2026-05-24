using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Users.Infrastructure;
using BarkCloud.Users.Persistence.Services;

using MediatR;

namespace BarkCloud.Users.Features.DeleteAccount;

public class DeleteAccountCommandHandler : IRequestHandler<DeleteAccountCommand>
{
    private readonly UsersStorage _usersStorage;
    private readonly UserContext _userContext;
    private readonly UserInfoQueueSender _userInfoQueueSender;
    private readonly MetricsCollector _metrics;
    private readonly ILogger<DeleteAccountCommandHandler> _logger;

    public DeleteAccountCommandHandler(UsersStorage usersStorage, UserContext userContext,
        UserInfoQueueSender userInfoQueueSender, MetricsCollector metrics,
        ILogger<DeleteAccountCommandHandler> logger)
    {
        _usersStorage = usersStorage;
        _userContext = userContext;
        _userInfoQueueSender = userInfoQueueSender;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task Handle(DeleteAccountCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Начало удаления аккаунта пользователя {UserId}", _userContext.UserId);

        // Удаляем профиль, контакт, устройства и настройки приватности (каскадно в Users).
        await _usersStorage.DeleteUser(_userContext.UserId);

        // Публикуем событие, чтобы остальные сервисы (Identity — пароли/сессии, Files — хранилище)
        // могли очистить свои данные пользователя.
        await _userInfoQueueSender.UserDeletedEvent(_userContext.UserId);

        _metrics.Increment("accounts_deleted");

        _logger.LogInformation("Аккаунт пользователя {UserId} удалён, событие UserDeleted опубликовано", _userContext.UserId);
    }
}
