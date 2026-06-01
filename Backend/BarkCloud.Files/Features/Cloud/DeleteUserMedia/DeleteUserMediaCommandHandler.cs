using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.DeleteUserMedia;

/// <summary>
/// Удаляет медиа из галереи пользователя по file_id (то, что показывает ListUserMedia).
/// <para>
/// • Если у блоба есть «живые» записи каталога владельца — мягко удаляет их все
///   (перенос в корзину, восстановимо; квота держится до окончательной зачистки) —
///   как <see cref="DeleteFileEntry.DeleteFileEntryCommandHandler"/>, но по file_id.
/// </para>
/// <para>
/// • Если записей нет (медиа загружено без привязки к папке) — снимает владельца
///   с блоба (<see cref="IUploadedFilesStorage.RemoveUploaderFromFile"/>): жёсткое
///   удаление из галереи, освобождает квоту, без возможности восстановления.
/// </para>
/// </summary>
public class DeleteUserMediaCommandHandler : IRequestHandler<DeleteUserMediaCommand, CloudEmpty>
{
    private readonly ICloudHierarchyStorage _cloudHierarchy;
    private readonly IUploadedFilesStorage _uploadedFiles;
    private readonly IAlbumStorage _albumStorage;
    private readonly UserContext _userContext;
    private readonly ILogger<DeleteUserMediaCommandHandler> _logger;

    public DeleteUserMediaCommandHandler(
        ICloudHierarchyStorage cloudHierarchy,
        IUploadedFilesStorage uploadedFiles,
        IAlbumStorage albumStorage,
        UserContext userContext,
        ILogger<DeleteUserMediaCommandHandler> logger)
    {
        _cloudHierarchy = cloudHierarchy;
        _uploadedFiles = uploadedFiles;
        _albumStorage = albumStorage;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(DeleteUserMediaCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        var liveEntries = await _cloudHierarchy.GetLiveEntriesForFile(ownerId, request.FileId, cancellationToken);

        if (liveEntries.Count > 0)
        {
            var now = DateTime.UtcNow;
            foreach (var entry in liveEntries)
            {
                entry.IsDeleted = true;
                entry.DeletedAt = now;
                entry.PurgeAt = now + TrashPurgeService.Retention;
            }

            await _cloudHierarchy.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "DeleteUserMedia: {Count} записей файла {FileId} (Owner: {OwnerId}) перемещены в корзину",
                liveEntries.Count, request.FileId, ownerId);
        }
        else
        {
            // Жёсткое удаление (нет записей каталога): помимо снятия владельца с блоба чистим
            // членство файла во всех альбомах владельца и переустанавливаем обложки — иначе
            // остаётся осиротевшая запись AlbumItem (раздувает счётчик, ломает обложку альбома).
            var removedFromAlbums = await _albumStorage.RemoveFileFromAllAlbums(ownerId, request.FileId, cancellationToken);
            await _uploadedFiles.RemoveUploaderFromFile(request.FileId, ownerId, cancellationToken);

            _logger.LogInformation(
                "DeleteUserMedia: владелец {OwnerId} снят с файла {FileId} (нет записей каталога); удалён из {AlbumCount} альбом(ов)",
                ownerId, request.FileId, removedFromAlbums);
        }

        return new CloudEmpty();
    }
}
