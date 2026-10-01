using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Persistence.Exceptions;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;

using MediatR;

namespace BarkCloud.Identity.Features.RemoveActiveSession;

public class RemoveActiveSessionCommandHandler : IRequestHandler<RemoveActiveSessionCommand, RemoveActiveSessionResponse>
{
    private readonly IRefreshTokensStorage _refreshTokensStorage;
    private readonly UserContext _userContext;
    private readonly UsersServerApi.UsersServerApiClient _usersClient;
    private readonly MetricsCollector _metrics;
    private readonly ILogger<RemoveActiveSessionCommandHandler> _logger;

    public RemoveActiveSessionCommandHandler(IRefreshTokensStorage refreshTokensStorage, UserContext userContext,
        UsersServerApi.UsersServerApiClient usersClient, MetricsCollector metrics,
        ILogger<RemoveActiveSessionCommandHandler> logger)
    {
        _refreshTokensStorage = refreshTokensStorage;
        _userContext = userContext;
        _usersClient = usersClient;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<RemoveActiveSessionResponse> Handle(RemoveActiveSessionCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Попытка удаления сессии по DeviceId {DeviceId} для пользователя {UserId}",
            request.DeviceId,
            _userContext.UserId
        );

        try
        {
            await _refreshTokensStorage.RevokeSession(request.DeviceId, _userContext.UserId, cancellationToken);

            _metrics.Increment("sessions_removed");
            _metrics.Increment("sessions_revoked");

            _logger.LogInformation(
                "Сессия для устройства {DeviceId} успешно удалена для пользователя {UserId}",
                request.DeviceId,
                _userContext.UserId
            );
        }
        catch (RefreshTokenNotFoundException ex)
        {
            _metrics.Increment("session_removal_failed_not_found");
            _logger.LogWarning(
                ex,
                "Сессия для устройства {DeviceId} не найдена для пользователя {UserId}",
                request.DeviceId,
                _userContext.UserId
            );
            throw new SessionNotFoundException();
        }

        // Удаляем устройство из Users сервиса
        try
        {
            await _usersClient.DeleteUserDeviceAsync(new DeleteUserDeviceRequest
            {
                DeviceId = request.DeviceId,
                UserId = _userContext.UserId
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Не удалось удалить устройство {DeviceId} из Users сервиса для пользователя {UserId}",
                request.DeviceId, _userContext.UserId);
        }

        return new RemoveActiveSessionResponse();
    }
}
