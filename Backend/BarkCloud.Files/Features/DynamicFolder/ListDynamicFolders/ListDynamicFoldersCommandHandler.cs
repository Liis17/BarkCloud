using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using MediatR;

using DomainFolder = BarkCloud.Files.Domain.DynamicFolder;

namespace BarkCloud.Files.Features.DynamicFolder.ListDynamicFolders;

public class ListDynamicFoldersCommandHandler : IRequestHandler<ListDynamicFoldersCommand, ListDynamicFoldersResponse>
{
    private readonly IDynamicFolderStorage _storage;
    private readonly DynamicFolderViewBuilder _viewBuilder;
    private readonly UserContext _userContext;

    public ListDynamicFoldersCommandHandler(
        IDynamicFolderStorage storage,
        DynamicFolderViewBuilder viewBuilder,
        UserContext userContext)
    {
        _storage = storage;
        _viewBuilder = viewBuilder;
        _userContext = userContext;
    }

    public async Task<ListDynamicFoldersResponse> Handle(ListDynamicFoldersCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        var folders = new List<DomainFolder>(SystemDynamicFolders.All());
        folders.AddRange(await _storage.ListFolders(ownerId, cancellationToken));

        var response = new ListDynamicFoldersResponse();
        var views = await _viewBuilder.BuildAsync(ownerId, folders, cancellationToken);
        response.Folders.AddRange(views);
        return response;
    }
}
