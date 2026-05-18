using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Files.Features.Cloud.DeleteFileEntry;

public class DeleteFileEntryCommandHandler : IRequestHandler<DeleteFileEntryCommand, CloudEmpty>
{
    private readonly CloudHierarchyStorage _storage;
    private readonly FilesContext _context;
    private readonly UserContext _userContext;
    private readonly ILogger<DeleteFileEntryCommandHandler> _logger;

    public DeleteFileEntryCommandHandler(
        CloudHierarchyStorage storage,
        FilesContext context,
        UserContext userContext,
        ILogger<DeleteFileEntryCommandHandler> logger)
    {
        _storage = storage;
        _context = context;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<CloudEmpty> Handle(DeleteFileEntryCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        var entry = await _storage.GetFileEntry(request.EntryId, cancellationToken);
        if (entry is null)
            throw new FileEntryNotFoundException();
        if (entry.OwnerId != ownerId)
            throw new CloudAccessDeniedException();

        // Декремент Uploaders на UploadFile: убираем OwnerId.
        // Если у пользователя осталась ещё одна привязка к этому же UploadFile —
        // не снимаем владельца, иначе квота будет посчитана неправильно.
        var fileId = entry.FileId;
        var otherEntriesExist = await _context.CloudFileEntries
            .AsNoTracking()
            .AnyAsync(x => x.OwnerId == ownerId && x.FileId == fileId && x.Id != entry.Id, cancellationToken);

        if (!otherEntriesExist)
        {
            var uploadFile = await _context.UploadedFiles
                .FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken);

            if (uploadFile is not null)
            {
                uploadFile.Uploaders.Remove(ownerId);
            }

            // Декремент и для всех привязанных превью.
            // Превью разделяются по SHA256 с другими оригиналами тех же байтов —
            // владельца можно безопасно снять: если у пользователя нет других ссылок
            // на этот оригинал, у него не должно остаться и ссылок на его превью.
            var previewFileIds = await _context.FilePreviews
                .AsNoTracking()
                .Where(p => p.OriginalFileId == fileId)
                .Select(p => p.PreviewFileId)
                .ToListAsync(cancellationToken);

            if (previewFileIds.Count > 0)
            {
                var previewFiles = await _context.UploadedFiles
                    .Where(f => previewFileIds.Contains(f.Id))
                    .ToListAsync(cancellationToken);

                foreach (var pf in previewFiles)
                    pf.Uploaders.Remove(ownerId);
            }
        }

        _context.CloudFileEntries.Remove(entry);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Удалена запись {EntryId} (FileId: {FileId}, Owner: {OwnerId}, OtherEntriesRemain: {OtherEntries})",
            entry.Id, fileId, ownerId, otherEntriesExist);

        return new CloudEmpty();
    }
}
