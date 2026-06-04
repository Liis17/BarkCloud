using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

namespace BarkCloud.Files.Features.DynamicFolder.DeleteDynamicFolder;

public class DeleteDynamicFolderCommandHandler : IRequestHandler<DeleteDynamicFolderCommand, CloudEmpty>
{
    private readonly IDynamicFolderStorage _storage;
    private readonly UserContext _userContext;
    private readonly ILogger<DeleteDynamicFolderCommandHandler> _logger;

    public DeleteDynamicFolderCommandHandler(
        IDynamicFolderStorage storage,
        UserContext userContext,
        ILogger<DeleteDynamicFolderCommandHandler> logger)
    {
        _storage = storage;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(DeleteDynamicFolderCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        var folder = await _storage.GetFolder(request.FolderId, cancellationToken);
        if (folder is null)
            throw new DynamicFolderNotFoundException();
        if (folder.OwnerId != ownerId)
            throw new CloudAccessDeniedException();

        await _storage.RemoveFolder(folder, cancellationToken);

        _logger.LogInformation("Удалена умная папка {FolderId} (Owner: {OwnerId})", folder.Id, ownerId);

        return new CloudEmpty();
    }
}
