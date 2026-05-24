using BarkCloud.Proto.Users;

using MediatR;

namespace BarkCloud.Users.Features.Devices.DeleteDevice;

public class DeleteDeviceCommand : IRequest<DeleteDeviceResponse>
{
    public Guid DeviceId { get; set; }
}
