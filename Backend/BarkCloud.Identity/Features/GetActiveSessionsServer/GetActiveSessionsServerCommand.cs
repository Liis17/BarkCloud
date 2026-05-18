using BarkCloud.Proto.Identity;

using MediatR;

namespace BarkCloud.Identity.Features.GetActiveSessionsServer;

public class GetActiveSessionsServerCommand : IRequest<GetActiveSessionsResponse>
{
    public long UserId { get; set; }
}
