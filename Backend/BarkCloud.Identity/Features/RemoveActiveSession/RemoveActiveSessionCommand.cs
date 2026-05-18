using BarkCloud.Proto.Identity;

using MediatR;

namespace BarkCloud.Identity.Features.RemoveActiveSession;

public class RemoveActiveSessionCommand : IRequest<RemoveActiveSessionResponse>
{
    public string DeviceId { get; set; }
}
