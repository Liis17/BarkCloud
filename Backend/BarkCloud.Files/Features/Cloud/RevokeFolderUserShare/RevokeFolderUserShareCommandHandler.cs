using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.RevokeFolderUserShare;

public class RevokeFolderUserShareCommandHandler : IRequestHandler<RevokeFolderUserShareCommand, CloudEmpty>
{
    private readonly IDirectoryGrantStorage _dirGrants;
    private readonly UserContext _userContext;

    public RevokeFolderUserShareCommandHandler(IDirectoryGrantStorage dirGrants, UserContext userContext)
    {
        _dirGrants = dirGrants;
        _userContext = userContext;
    }

    public async Task<CloudEmpty> Handle(RevokeFolderUserShareCommand request, CancellationToken cancellationToken)
    {
        await _dirGrants.Remove(_userContext.UserId, request.GrantId, cancellationToken);
        return new CloudEmpty();
    }
}
