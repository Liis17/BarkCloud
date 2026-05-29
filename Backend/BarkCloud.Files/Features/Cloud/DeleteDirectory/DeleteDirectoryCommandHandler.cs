using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Features.Cloud.DeleteDirectory;

/// <summary>
/// Удаляет папку рекурсивно: все файлы поддерева перемещаются в корзину (мягкое удаление,
/// сохраняют владение/квоту и подлежат восстановлению), а сами папки удаляются сразу.
/// Восстановление файла из удалённой папки вернёт его в корень владельца.
/// </summary>
public class DeleteDirectoryCommandHandler : IRequestHandler<DeleteDirectoryCommand, CloudEmpty>
{
    private readonly ICloudHierarchyStorage _storage;
    private readonly UserContext _userContext;
    private readonly ILogger<DeleteDirectoryCommandHandler> _logger;

    public DeleteDirectoryCommandHandler(
        ICloudHierarchyStorage storage,
        UserContext userContext,
        ILogger<DeleteDirectoryCommandHandler> logger)
    {
        _storage = storage;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(DeleteDirectoryCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        var root = await _storage.GetDirectoryAsNoTracking(request.DirectoryId, cancellationToken);
        if (root is null)
            throw new DirectoryNotFoundException();
        if (root.OwnerId != ownerId)
            throw new CloudAccessDeniedException();

        // Собираем всё поддерево
        var subtree = await _storage.GetSubtree(ownerId, root.Id, cancellationToken);
        var subtreeIds = subtree.Select(d => d.Id).ToList();

        // Все живые файлы-записи во всём поддереве → в корзину.
        var entries = await _storage.GetFileEntriesInDirectories(ownerId, subtreeIds, cancellationToken);

        var now = DateTime.UtcNow;
        var purgeAt = now + TrashPurgeService.Retention;
        foreach (var entry in entries)
        {
            entry.IsDeleted = true;
            entry.DeletedAt = now;
            entry.PurgeAt = purgeAt;
        }

        // Папки удаляем сразу (структура не сохраняется; restore вернёт файлы в корень).
        _storage.RemoveDirectories(subtree);

        await _storage.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Удалена папка {DirectoryId} рекурсивно (директорий: {DirCount}, файлов в корзину: {FileCount}, Owner: {OwnerId})",
            root.Id, subtree.Count, entries.Count, ownerId);

        return new CloudEmpty();
    }
}
