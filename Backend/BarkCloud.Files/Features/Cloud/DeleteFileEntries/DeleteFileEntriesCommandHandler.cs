using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.DeleteFileEntries;

/// <summary>
/// Массовое мягкое удаление: перемещает в корзину все указанные записи одним запросом
/// (та же семантика, что и единичный <see cref="DeleteFileEntry.DeleteFileEntryCommand"/> —
/// блоб и владение сохраняются, файлы восстановимы до истечения PurgeAt). Чужие,
/// несуществующие и уже удалённые id молча пропускаются, чтобы один «плохой» id не
/// валил весь пакет; возвращается число реально перемещённых записей.
/// </summary>
public class DeleteFileEntriesCommandHandler : IRequestHandler<DeleteFileEntriesCommand, DeleteFileEntriesResponse>
{
    private readonly ICloudHierarchyStorage _storage;
    private readonly UserContext _userContext;
    private readonly ILogger<DeleteFileEntriesCommandHandler> _logger;

    public DeleteFileEntriesCommandHandler(
        ICloudHierarchyStorage storage,
        UserContext userContext,
        ILogger<DeleteFileEntriesCommandHandler> logger)
    {
        _storage = storage;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<DeleteFileEntriesResponse> Handle(DeleteFileEntriesCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        var ids = request.EntryIds.Distinct().ToList();
        if (ids.Count == 0)
            return new DeleteFileEntriesResponse { DeletedCount = 0 };

        var entries = await _storage.GetLiveFileEntriesByIds(ownerId, ids, cancellationToken);

        var now = DateTime.UtcNow;
        var purgeAt = now + TrashPurgeService.Retention;
        foreach (var entry in entries)
        {
            entry.IsDeleted = true;
            entry.DeletedAt = now;
            entry.PurgeAt = purgeAt;
        }

        await _storage.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Массовое удаление: запрошено {RequestedCount} записей, перемещено в корзину {DeletedCount} (Owner: {OwnerId}, PurgeAt={PurgeAt})",
            ids.Count, entries.Count, ownerId, purgeAt);

        return new DeleteFileEntriesResponse { DeletedCount = entries.Count };
    }
}
