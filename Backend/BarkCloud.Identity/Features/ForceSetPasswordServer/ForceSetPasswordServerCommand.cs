using BarkCloud.Proto.Identity;

using MediatR;

namespace BarkCloud.Identity.Features.ForceSetPasswordServer;

public class ForceSetPasswordServerCommand : IRequest<ForceSetPasswordServerResponse>
{
    public long UserId { get; set; }
    public string NewPassword { get; set; } = string.Empty;
}
