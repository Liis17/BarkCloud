using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Identity.Services;
using BarkCloud.Proto.Identity;

using Grpc.Core;

using MediatR;

namespace BarkCloud.Identity.Features.CreateSessionForUserServer;

public class CreateSessionForUserServerCommandHandler(
    SessionIssuer sessionIssuer,
    MetricsCollector metrics,
    ILogger<CreateSessionForUserServerCommandHandler> logger)
    : IRequestHandler<CreateSessionForUserServerCommand, CreateSessionForUserServerResponse>
{
    public async Task<CreateSessionForUserServerResponse> Handle(CreateSessionForUserServerCommand request, CancellationToken cancellationToken)
    {
        if (request.UserId <= 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "UserId is required"));
        }

        if (string.IsNullOrEmpty(request.DeviceId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "DeviceId is required"));
        }

        if (string.IsNullOrEmpty(request.DeviceName))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "DeviceName is required"));
        }

        if (string.IsNullOrEmpty(request.OperationSystem))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "OperationSystem is required"));
        }

        if (string.IsNullOrEmpty(request.AppName))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "AppName is required"));
        }

        logger.LogInformation(
            "Создание серверной сессии для пользователя {UserId} на устройстве {DeviceId} ({DeviceName})",
            request.UserId, request.DeviceId, request.DeviceName);

        var session = await sessionIssuer.IssueAsync(
            request.UserId,
            new SessionDevice(request.DeviceId, request.DeviceName, request.OperationSystem, request.AppName, request.IpAddress),
            cancellationToken);

        metrics.Increment("server_sessions_created");

        logger.LogInformation(
            "Серверная сессия успешно создана для пользователя {UserId}, устройство {DeviceId}",
            request.UserId, request.DeviceId);

        return new CreateSessionForUserServerResponse
        {
            AccessToken = session.AccessToken,
            RefreshToken = session.RefreshToken
        };
    }
}
