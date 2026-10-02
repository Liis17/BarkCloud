using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Features.Cloud.MoveDirectory;

public class MoveDirectoryCommandHandler : IRequestHandler<MoveDirectoryCommand, CloudEmpty>
{
    private readonly ICloudHierarchyStorage _storage;
    private readonly UserContext _userContext;
    private readonly ILogger<MoveDirectoryCommandHandler> _logger;

    public MoveDirectoryCommandHandler(
        ICloudHierarchyStorage storage,
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

        // Проверка предков и запись ParentId — одна граница: параллельное перемещение
        // не должно пройти проверку по состоянию до нашей записи (иначе возможен цикл).
        await using var treeLock = await _storage.LockTree(ownerId, cancellationToken);

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
            // Повторно встреченная папка — цепочка предков уже зациклена (повреждённые данные):
            // подвешивать папку к такой цепочке нельзя.
            var visited = new HashSet<Guid> { newParent.Id };
            var cursorId = newParent.ParentId;
            while (cursorId.HasValue)
            {
                if (cursorId.Value == directory.Id || !visited.Add(cursorId.Value))
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
        await treeLock.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Перемещена папка {DirectoryId} в {NewParentId}",
            directory.Id, request.NewParentId);

        return new CloudEmpty();
    }
}
