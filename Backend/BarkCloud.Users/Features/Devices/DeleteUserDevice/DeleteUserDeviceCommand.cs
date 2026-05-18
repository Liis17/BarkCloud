using BarkCloud.Proto.Users;

using MediatR;

namespace BarkCloud.Users.Features.Devices.DeleteUserDevice;

public class DeleteUserDeviceCommand : IRequest<DeleteUserDeviceResponse>
{
    public Guid DeviceId { get; set; }
    public long UserId { get; set; }
}
