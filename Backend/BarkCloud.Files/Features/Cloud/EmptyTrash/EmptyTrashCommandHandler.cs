using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.EmptyTrash;

/// <summary>
/// Очищает корзину владельца целиком: окончательно удаляет все записи в корзине вместе
/// с осиротевшими блобами и превью из S3.
/// </summary>
public class EmptyTrashCommandHandler : IRequestHandler<EmptyTrashCommand, CloudEmpty>
{
    private readonly ICloudHierarchyStorage _storage;
    private readonly TrashPurgeService _purge;
    private readonly UserContext _userContext;
    private readonly ILogger<EmptyTrashCommandHandler> _logger;

    public EmptyTrashCommandHandler(
        ICloudHierarchyStorage storage,
        TrashPurgeService purge,
        UserContext userContext,
        ILogger<EmptyTrashCommandHandler> logger)
    {
        _storage = storage;
        _purge = purge;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(EmptyTrashCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        var entries = await _storage.GetAllTrashedEntries(ownerId, cancellationToken);
        var blobs = await _purge.PurgeEntriesAsync(entries, cancellationToken);

        _logger.LogInformation(
            "Корзина владельца {OwnerId} очищена: записей {Entries}, блобов из S3 {Blobs}",
            ownerId, entries.Count, blobs);

        return new CloudEmpty();
    }
}
