using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

using DomainMediaKind = BarkCloud.Files.Domain.MediaKind;
using FileNotFoundException = BarkCloud.Shared.Exceptions.Files.FileNotFoundException;

namespace BarkCloud.Files.Features.Cloud.DeleteUserMedia;

/// <summary>
/// Удаляет медиа из галереи пользователя по file_id (то, что показывает ListUserMedia).
/// <para>
/// • Если у блоба есть «живые» записи каталога владельца — мягко удаляет их все
///   (перенос в корзину, восстановимо; квота держится до окончательной зачистки) —
///   как <see cref="DeleteFileEntry.DeleteFileEntryCommandHandler"/>, но по file_id.
/// </para>
/// <para>
/// • Если записей нет (медиа загружено без привязки к папке) — создаёт запись
///   сразу в корзине, чтобы удаление оставалось восстановимым и физическая
///   зачистка blob выполнялась только через корзину.
/// </para>
/// </summary>
public class DeleteUserMediaCommandHandler : IRequestHandler<DeleteUserMediaCommand, CloudEmpty>
{
    private readonly ICloudHierarchyStorage _cloudHierarchy;
    private readonly IUploadedFilesStorage _uploadedFiles;
    private readonly UserContext _userContext;
    private readonly ILogger<DeleteUserMediaCommandHandler> _logger;

    public DeleteUserMediaCommandHandler(
        ICloudHierarchyStorage cloudHierarchy,
        IUploadedFilesStorage uploadedFiles,
        UserContext userContext,
        ILogger<DeleteUserMediaCommandHandler> logger)
    {
        _cloudHierarchy = cloudHierarchy;
        _uploadedFiles = uploadedFiles;
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
            var existingEntries = await _cloudHierarchy.GetEntriesForFiles(
                ownerId, new[] { request.FileId }, cancellationToken);
            if (existingEntries.Count > 0)
                return new CloudEmpty();

            var file = await _uploadedFiles.GetFile(request.FileId);
            if (file is null)
                throw new FileNotFoundException();
            if (!file.Uploaders.Contains(ownerId))
                throw new CloudAccessDeniedException();

            var (systemKind, folderName) = MapMediaKindToSystemFolder(file.MediaKind);
            var directoryId = await _cloudHierarchy.EnsureSystemDirectory(ownerId, systemKind, folderName, cancellationToken);
            var now = DateTime.UtcNow;
            var entry = new CloudFileEntry
            {
                Id = Guid.NewGuid(),
                OwnerId = ownerId,
                DirectoryId = directoryId,
                FileId = request.FileId,
                Name = string.IsNullOrWhiteSpace(file.Filename) ? request.FileId.ToString() : file.Filename,
                CreatedAt = now,
                IsDeleted = true,
                DeletedAt = now,
                PurgeAt = now + TrashPurgeService.Retention
            };

            await _cloudHierarchy.AddFileEntry(entry, cancellationToken);

            _logger.LogInformation(
                "DeleteUserMedia: для файла {FileId} (Owner: {OwnerId}) создана запись корзины {EntryId}",
                request.FileId, ownerId, entry.Id);
        }

        return new CloudEmpty();
    }

    private static (CloudDirectorySystemKind kind, string name) MapMediaKindToSystemFolder(DomainMediaKind mediaKind) => mediaKind switch
    {
        DomainMediaKind.Photo => (CloudDirectorySystemKind.Photos, "Фото"),
        DomainMediaKind.Video => (CloudDirectorySystemKind.Videos, "Видео"),
        _ => (CloudDirectorySystemKind.OtherDocuments, "Другие документы"),
    };
}
