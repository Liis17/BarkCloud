using BarkCloud.Proto.Users;

using MediatR;

namespace BarkCloud.Users.Features.Devices.RenameDevice;

public class RenameDeviceCommand : IRequest<RenameDeviceResponse>
{
    public Guid DeviceId { get; set; }
    public string CustomName { get; set; }
}
