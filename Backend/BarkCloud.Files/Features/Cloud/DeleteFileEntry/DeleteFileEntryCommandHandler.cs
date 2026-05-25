using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.DeleteFileEntry;

/// <summary>
/// Перемещает запись о файле в корзину (мягкое удаление). Запись пропадает из иерархии,
/// галереи и альбомов, но блоб и владение (Uploaders) сохраняются — квота не освобождается,
/// файл можно восстановить. Окончательная зачистка из БД и S3 происходит при истечении
/// PurgeAt (фоновый воркер) либо по явному «Удалить навсегда».
/// </summary>
public class DeleteFileEntryCommandHandler : IRequestHandler<DeleteFileEntryCommand, CloudEmpty>
{
    private readonly CloudHierarchyStorage _storage;
    private readonly UserContext _userContext;
    private readonly ILogger<DeleteFileEntryCommandHandler> _logger;

    public DeleteFileEntryCommandHandler(
        CloudHierarchyStorage storage,
        UserContext userContext,
        ILogger<DeleteFileEntryCommandHandler> logger)
    {
        _storage = storage;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(DeleteFileEntryCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        var entry = await _storage.GetFileEntry(request.EntryId, cancellationToken);
        if (entry is null || entry.IsDeleted)
            throw new FileEntryNotFoundException();
        if (entry.OwnerId != ownerId)
            throw new CloudAccessDeniedException();

        var now = DateTime.UtcNow;
        entry.IsDeleted = true;
        entry.DeletedAt = now;
        entry.PurgeAt = now + TrashPurgeService.Retention;

        await _storage.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Запись {EntryId} (FileId: {FileId}, Owner: {OwnerId}) перемещена в корзину, PurgeAt={PurgeAt}",
            entry.Id, entry.FileId, ownerId, entry.PurgeAt);

        return new CloudEmpty();
    }
}
