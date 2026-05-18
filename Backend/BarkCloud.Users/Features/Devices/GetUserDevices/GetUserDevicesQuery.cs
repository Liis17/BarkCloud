using BarkCloud.Proto.Users;

using MediatR;

namespace BarkCloud.Users.Features.Devices.GetUserDevices;

public class GetUserDevicesQuery : IRequest<GetUserDevicesResponse>
{
    public long UserId { get; set; }
}
