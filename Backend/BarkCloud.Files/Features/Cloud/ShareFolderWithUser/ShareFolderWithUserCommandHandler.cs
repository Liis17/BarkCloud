using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Features.Cloud.ShareFolderWithUser;

public class ShareFolderWithUserCommandHandler : IRequestHandler<ShareFolderWithUserCommand, CloudEmpty>
{
    private readonly IDirectoryGrantStorage _dirGrants;
    private readonly ICloudHierarchyStorage _hierarchy;
    private readonly UserContext _userContext;
    private readonly ILogger<ShareFolderWithUserCommandHandler> _logger;

    public ShareFolderWithUserCommandHandler(
        IDirectoryGrantStorage dirGrants,
        ICloudHierarchyStorage hierarchy,
        UserContext userContext,
        ILogger<ShareFolderWithUserCommandHandler> logger)
    {
        _dirGrants = dirGrants;
        _hierarchy = hierarchy;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(ShareFolderWithUserCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        // Поделиться можно только своей папкой.
        var dir = await _hierarchy.GetDirectoryAsNoTracking(request.DirectoryId, cancellationToken);
        if (dir is null)
            throw new DirectoryNotFoundException();
        if (dir.OwnerId != ownerId)
            throw new CloudAccessDeniedException();

        // Шаринг самому себе бессмыслен — тихо игнорируем.
        if (request.RecipientUserId == ownerId)
            return new CloudEmpty();

        // Идемпотентность: если грант уже есть — ничего не делаем.
        if (await _dirGrants.Exists(ownerId, request.DirectoryId, request.RecipientUserId, cancellationToken))
            return new CloudEmpty();

        await _dirGrants.Add(new DirectoryGrant
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            RecipientId = request.RecipientUserId,
            DirectoryId = request.DirectoryId,
            CreatedAt = DateTime.UtcNow
        }, cancellationToken);

        _logger.LogInformation(
            "Папка {DirectoryId} расшарена пользователю {RecipientId} (Owner: {OwnerId})",
            request.DirectoryId, request.RecipientUserId, ownerId);

        return new CloudEmpty();
    }
}
