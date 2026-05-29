using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.DeleteFromTrash;

/// <summary>
/// Удаляет один файл из корзины окончательно (немедленно): запись, превью, привязки к альбомам
/// и — если блоб осиротел — сам объект из S3.
/// </summary>
public class DeleteFromTrashCommandHandler : IRequestHandler<DeleteFromTrashCommand, CloudEmpty>
{
    private readonly ICloudHierarchyStorage _storage;
    private readonly TrashPurgeService _purge;
    private readonly UserContext _userContext;
    private readonly ILogger<DeleteFromTrashCommandHandler> _logger;

    public DeleteFromTrashCommandHandler(
        ICloudHierarchyStorage storage,
        TrashPurgeService purge,
        UserContext userContext,
        ILogger<DeleteFromTrashCommandHandler> logger)
    {
        _storage = storage;
        _purge = purge;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(DeleteFromTrashCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        var entry = await _storage.GetTrashedEntry(request.EntryId, cancellationToken);
        if (entry is null)
            throw new FileEntryNotFoundException();
        if (entry.OwnerId != ownerId)
            throw new CloudAccessDeniedException();

        await _purge.PurgeEntriesAsync(new[] { entry }, cancellationToken);

        _logger.LogInformation("Запись {EntryId} (Owner: {OwnerId}) удалена из корзины навсегда", entry.Id, ownerId);

        return new CloudEmpty();
    }
}
