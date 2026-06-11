using BarkCloud.Files.Domain;
using BarkCloud.Files.Helpers;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.RestoreFromTrash;

/// <summary>
/// Восстанавливает файл из корзины. Если исходная папка была удалена — файл возвращается
/// в корень владельца. Конфликт имени в целевой папке разрешается добавлением суффикса.
/// Если у владельца уже есть живая запись на тот же блоб (нарушение инварианта «одна
/// директория на файл») — восстановление отклоняется.
/// </summary>
public class RestoreFromTrashCommandHandler : IRequestHandler<RestoreFromTrashCommand, CloudEmpty>
{
    private readonly ICloudHierarchyStorage _storage;
    private readonly UserContext _userContext;
    private readonly FileActivityWriter _activity;
    private readonly ILogger<RestoreFromTrashCommandHandler> _logger;

    public RestoreFromTrashCommandHandler(
        ICloudHierarchyStorage storage,
        UserContext userContext,
        ILogger<RestoreFromTrashCommandHandler> logger,
        FileActivityWriter? activity = null)
    {
        _storage = storage;
        _userContext = userContext;
        _activity = activity ?? FileActivityWriter.Noop;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(RestoreFromTrashCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        var entry = await _storage.GetTrashedEntry(request.EntryId, cancellationToken);
        if (entry is null)
            throw new FileEntryNotFoundException();
        if (entry.OwnerId != ownerId)
            throw new CloudAccessDeniedException();

        // Инвариант «одна директория на файл»: нельзя восстановить, если уже есть живая запись.
        if (await _storage.FileEntryExistsForFile(ownerId, entry.FileId, cancellationToken))
            throw new FileAlreadyAttachedException();

        // Целевая папка: исходная, если ещё существует; иначе корень владельца.
        var targetDirectoryId = CloudHierarchyStorage.RootDirectoryId;
        if (entry.DirectoryId != CloudHierarchyStorage.RootDirectoryId)
        {
            var dir = await _storage.GetDirectoryAsNoTracking(entry.DirectoryId, cancellationToken);
            if (dir is not null && dir.OwnerId == ownerId)
                targetDirectoryId = dir.Id;
        }

        // Разрешаем конфликт имени среди живых записей в целевой папке.
        var name = await UniqueNameResolver.ResolveAsync(
            entry.Name,
            (candidate, ct) => _storage.FileEntryNameExists(ownerId, targetDirectoryId, candidate, ct),
            cancellationToken);

        entry.IsDeleted = false;
        entry.DeletedAt = null;
        entry.PurgeAt = null;
        entry.DirectoryId = targetDirectoryId;
        entry.Name = name;

        await _storage.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Запись {EntryId} (FileId: {FileId}, Owner: {OwnerId}) восстановлена из корзины в директорию {DirectoryId} как {Name}",
            entry.Id, entry.FileId, ownerId, targetDirectoryId, name);

        await _activity.AddAsync(
            ownerId,
            entry.FileId,
            ownerId,
            FileActivityKind.Restored,
            "Восстановлен из корзины",
            entry.Id,
            new { entry.Name, entry.DirectoryId },
            cancellationToken);

        return new CloudEmpty();
    }
}
