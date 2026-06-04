using BarkCloud.Files.Mapping;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

using DomainFolder = BarkCloud.Files.Domain.DynamicFolder;

namespace BarkCloud.Files.Features.DynamicFolder.CreateDynamicFolder;

public class CreateDynamicFolderCommandHandler : IRequestHandler<CreateDynamicFolderCommand, DynamicFolderInfo>
{
    private readonly IDynamicFolderStorage _storage;
    private readonly DynamicFolderViewBuilder _viewBuilder;
    private readonly UserContext _userContext;
    private readonly ILogger<CreateDynamicFolderCommandHandler> _logger;

    public CreateDynamicFolderCommandHandler(
        IDynamicFolderStorage storage,
        DynamicFolderViewBuilder viewBuilder,
        UserContext userContext,
        ILogger<CreateDynamicFolderCommandHandler> logger)
    {
        _storage = storage;
        _viewBuilder = viewBuilder;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<DynamicFolderInfo> Handle(CreateDynamicFolderCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var name = (request.Name ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(name))
            throw new DynamicFolderNameConflictException();

        if (await _storage.FolderNameExists(ownerId, name, cancellationToken))
            throw new DynamicFolderNameConflictException();

        foreach (var rule in request.Criteria.Rules)
            if (!DynamicFolderQueryBuilder.IsRuleValid(rule))
                throw new DynamicFolderInvalidCriteriaException();

        var now = DateTime.UtcNow;
        var folder = new DomainFolder
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            Name = name,
            IsSystem = false,
            SystemKey = null,
            Criteria = request.Criteria,
            IconKey = string.IsNullOrWhiteSpace(request.IconKey) ? null : request.IconKey.Trim(),
            CoverColor = string.IsNullOrWhiteSpace(request.CoverColor) ? null : request.CoverColor.Trim(),
            SortOrder = await _storage.GetMaxSortOrder(ownerId, cancellationToken) + 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _storage.AddFolder(folder, cancellationToken);

        _logger.LogInformation("Создана умная папка {FolderId} (Name: {Name}, Owner: {OwnerId}, Rules: {Rules})",
            folder.Id, folder.Name, ownerId, folder.Criteria.Rules.Count);

        var view = await _viewBuilder.BuildAsync(ownerId, new[] { folder }, cancellationToken);
        return view[0];
    }
}
