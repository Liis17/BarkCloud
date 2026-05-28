using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Users;
using BarkCloud.Users.Persistence.Services;

using MediatR;

namespace BarkCloud.Users.Features.Devices.DeleteDevice;

public class DeleteDeviceCommandHandler(
    IDevicesStorage devicesStorage,
    UserContext userContext,
    ILogger<DeleteDeviceCommandHandler> logger)
    : IRequestHandler<DeleteDeviceCommand, DeleteDeviceResponse>
{
    public async Task<DeleteDeviceResponse> Handle(DeleteDeviceCommand request, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Удаление собственного устройства {DeviceId} пользователем {UserId}",
            request.DeviceId, userContext.UserId);

        await devicesStorage.DeleteDevice(request.DeviceId, userContext.UserId);

        logger.LogInformation(
            "Устройство {DeviceId} удалено пользователем {UserId}",
            request.DeviceId, userContext.UserId);

        return new DeleteDeviceResponse();
    }
}
