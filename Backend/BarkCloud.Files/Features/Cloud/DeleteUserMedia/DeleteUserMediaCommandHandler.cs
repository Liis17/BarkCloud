using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using MediatR;

using Microsoft.EntityFrameworkCore;

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
    private readonly FilesContext _context;
    private readonly IUploadedFilesStorage _uploadedFiles;
    private readonly UserContext _userContext;
    private readonly ILogger<DeleteUserMediaCommandHandler> _logger;

    public DeleteUserMediaCommandHandler(
        FilesContext context,
        IUploadedFilesStorage uploadedFiles,
        UserContext userContext,
        ILogger<DeleteUserMediaCommandHandler> logger)
    {
        _context = context;
        _uploadedFiles = uploadedFiles;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(DeleteUserMediaCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        var liveEntries = await _context.CloudFileEntries
            .Where(e => e.OwnerId == ownerId && e.FileId == request.FileId && !e.IsDeleted)
            .ToListAsync(cancellationToken);

        if (liveEntries.Count > 0)
        {
            var now = DateTime.UtcNow;
            foreach (var entry in liveEntries)
            {
                entry.IsDeleted = true;
                entry.DeletedAt = now;
                entry.PurgeAt = now + TrashPurgeService.Retention;
            }

            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "DeleteUserMedia: {Count} записей файла {FileId} (Owner: {OwnerId}) перемещены в корзину",
                liveEntries.Count, request.FileId, ownerId);
        }
        else
        {
            await _uploadedFiles.RemoveUploaderFromFile(request.FileId, ownerId, cancellationToken);

            _logger.LogInformation(
                "DeleteUserMedia: владелец {OwnerId} снят с файла {FileId} (нет записей каталога)",
                ownerId, request.FileId);
        }

        return new CloudEmpty();
    }
}
