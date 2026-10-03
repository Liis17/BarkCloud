using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.GetUserStorageInfo;

public class GetUserStorageInfoCommand : IRequest<GetUserStorageInfoResponse>
{
    public bool NonBlockingStats { get; init; }
}
