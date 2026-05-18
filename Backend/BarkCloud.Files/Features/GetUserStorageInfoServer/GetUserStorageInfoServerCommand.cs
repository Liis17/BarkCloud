using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.GetUserStorageInfoServer;

public class GetUserStorageInfoServerCommand : IRequest<GetUserStorageInfoResponse>
{
    public long UserId { get; set; }
}
