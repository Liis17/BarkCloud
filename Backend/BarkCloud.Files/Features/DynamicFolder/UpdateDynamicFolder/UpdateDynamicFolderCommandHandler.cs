using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

namespace BarkCloud.Files.Features.DynamicFolder.UpdateDynamicFolder;

public class UpdateDynamicFolderCommandHandler : IRequestHandler<UpdateDynamicFolderCommand, DynamicFolderInfo>
{
    private readonly IDynamicFolderStorage _storage;
    private readonly DynamicFolderViewBuilder _viewBuilder;
    private readonly UserContext _userContext;
    private readonly ILogger<UpdateDynamicFolderCommandHandler> _logger;

    public UpdateDynamicFolderCommandHandler(
        IDynamicFolderStorage storage,
        DynamicFolderViewBuilder viewBuilder,
        UserContext userContext,
        ILogger<UpdateDynamicFolderCommandHandler> logger)
    {
        _storage = storage;
        _viewBuilder = viewBuilder;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<DynamicFolderInfo> Handle(UpdateDynamicFolderCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        var folder = await _storage.GetFolder(request.FolderId, cancellationToken);
        if (folder is null)
            throw new DynamicFolderNotFoundException();
        if (folder.OwnerId != ownerId)
            throw new CloudAccessDeniedException();

        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (string.IsNullOrWhiteSpace(name))
                throw new DynamicFolderNameConflictException();
            if (name != folder.Name && await _storage.FolderNameExists(ownerId, name, cancellationToken))
                throw new DynamicFolderNameConflictException();
            folder.Name = name;
        }

        foreach (var rule in request.Criteria.Rules)
            if (!DynamicFolderQueryBuilder.IsRuleValid(rule))
                throw new DynamicFolderInvalidCriteriaException();

        folder.Criteria = request.Criteria;

        if (request.IconKey is not null)
            folder.IconKey = string.IsNullOrWhiteSpace(request.IconKey) ? null : request.IconKey.Trim();
        if (request.CoverColor is not null)
            folder.CoverColor = string.IsNullOrWhiteSpace(request.CoverColor) ? null : request.CoverColor.Trim();

        folder.UpdatedAt = DateTime.UtcNow;
        await _storage.UpdateFolder(folder, cancellationToken);

        _logger.LogInformation("Обновлена умная папка {FolderId} (Owner: {OwnerId})", folder.Id, ownerId);

        var view = await _viewBuilder.BuildAsync(ownerId, new[] { folder }, cancellationToken);
        return view[0];
    }
}
