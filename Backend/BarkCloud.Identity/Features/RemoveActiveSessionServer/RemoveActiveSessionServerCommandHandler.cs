using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Identity.Persistence.Exceptions;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;

using MediatR;

namespace BarkCloud.Identity.Features.RemoveActiveSessionServer;

public class RemoveActiveSessionServerCommandHandler : IRequestHandler<RemoveActiveSessionServerCommand, RemoveActiveSessionResponse>
{
    private readonly IRefreshTokensStorage _refreshTokensStorage;
    private readonly UsersServerApi.UsersServerApiClient _usersClient;
    private readonly MetricsCollector _metrics;
    private readonly ILogger<RemoveActiveSessionServerCommandHandler> _logger;

    public RemoveActiveSessionServerCommandHandler(IRefreshTokensStorage refreshTokensStorage,
        UsersServerApi.UsersServerApiClient usersClient, MetricsCollector metrics,
        ILogger<RemoveActiveSessionServerCommandHandler> logger)
    {
        _refreshTokensStorage = refreshTokensStorage;
        _usersClient = usersClient;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<RemoveActiveSessionResponse> Handle(RemoveActiveSessionServerCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Удаление сессии по DeviceId {DeviceId} для пользователя {UserId} (server)",
            request.DeviceId, request.UserId);

        try
        {
            await _refreshTokensStorage.RevokeSession(request.DeviceId, request.UserId, cancellationToken);
            _metrics.Increment("server_sessions_removed");
            _metrics.Increment("sessions_revoked");
        }
        catch (RefreshTokenNotFoundException ex)
        {
            _metrics.Increment("server_session_removal_failed_not_found");
            _logger.LogWarning(ex,
                "Сессия для устройства {DeviceId} не найдена для пользователя {UserId}",
                request.DeviceId, request.UserId);
            throw new SessionNotFoundException();
        }

        try
        {
            await _usersClient.DeleteUserDeviceAsync(new DeleteUserDeviceRequest
            {
                DeviceId = request.DeviceId,
                UserId = request.UserId
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Не удалось удалить устройство {DeviceId} из Users сервиса для пользователя {UserId}",
                request.DeviceId, request.UserId);
        }

        return new RemoveActiveSessionResponse();
    }
}
