using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Features.Cloud.MoveDirectory;

public class MoveDirectoryCommandHandler : IRequestHandler<MoveDirectoryCommand, CloudEmpty>
{
    private readonly CloudHierarchyStorage _storage;
    private readonly UserContext _userContext;
    private readonly ILogger<MoveDirectoryCommandHandler> _logger;

    public MoveDirectoryCommandHandler(
        CloudHierarchyStorage storage,
        UserContext userContext,
        ILogger<MoveDirectoryCommandHandler> logger)
    {
        _storage = storage;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(MoveDirectoryCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        var directory = await _storage.GetDirectory(request.DirectoryId, cancellationToken);
        if (directory is null)
            throw new DirectoryNotFoundException();
        if (directory.OwnerId != ownerId)
            throw new CloudAccessDeniedException();

        // No-op
        if (directory.ParentId == request.NewParentId)
            return new CloudEmpty();

        // Нельзя сделать папку родителем самой себя
        if (request.NewParentId == directory.Id)
            throw new CircularMoveException();

        if (request.NewParentId.HasValue)
        {
            var newParent = await _storage.GetDirectoryAsNoTracking(request.NewParentId.Value, cancellationToken);
            if (newParent is null)
                throw new DirectoryNotFoundException();
            if (newParent.OwnerId != ownerId)
                throw new CloudAccessDeniedException();

            // Проверяем, что новый родитель не лежит в поддереве перемещаемой папки.
            // Идём вверх от newParent — если встретим directory.Id, значит это цикл.
            var cursorId = newParent.ParentId;
            while (cursorId.HasValue)
            {
                if (cursorId.Value == directory.Id)
                    throw new CircularMoveException();

                var ancestor = await _storage.GetDirectoryAsNoTracking(cursorId.Value, cancellationToken);
                if (ancestor is null)
                    break;
                cursorId = ancestor.ParentId;
            }
        }

        // Проверка уникальности имени в новом родителе
        if (await _storage.DirectoryNameExists(ownerId, request.NewParentId, directory.Name, cancellationToken))
            throw new DirectoryNameConflictException();

        directory.ParentId = request.NewParentId;
        directory.UpdatedAt = DateTime.UtcNow;
        await _storage.UpdateDirectory(directory, cancellationToken);

        _logger.LogInformation(
            "Перемещена папка {DirectoryId} в {NewParentId}",
            directory.Id, request.NewParentId);

        return new CloudEmpty();
    }
}
