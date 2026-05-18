using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using MediatR;

using Microsoft.EntityFrameworkCore;

using DirectoryNotFoundException = BarkCloud.Shared.Exceptions.Files.DirectoryNotFoundException;

namespace BarkCloud.Files.Features.Cloud.DeleteDirectory;

public class DeleteDirectoryCommandHandler : IRequestHandler<DeleteDirectoryCommand, CloudEmpty>
{
    private readonly CloudHierarchyStorage _storage;
    private readonly UploadedFilesStorage _filesStorage;
    private readonly FilesContext _context;
    private readonly UserContext _userContext;
    private readonly ILogger<DeleteDirectoryCommandHandler> _logger;

    public DeleteDirectoryCommandHandler(
        CloudHierarchyStorage storage,
        UploadedFilesStorage filesStorage,
        FilesContext context,
        UserContext userContext,
        ILogger<DeleteDirectoryCommandHandler> logger)
    {
        _storage = storage;
        _filesStorage = filesStorage;
        _context = context;
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

        // Все файлы-записи во всём поддереве
        var entries = await _storage.GetFileEntriesInDirectories(ownerId, subtreeIds, cancellationToken);

        // Декремент Uploaders для каждого затронутого UploadFile
        var fileIds = entries.Select(e => e.FileId).Distinct().ToList();
        if (fileIds.Count > 0)
        {
            var uploadFiles = await _context.UploadedFiles
                .Where(f => fileIds.Contains(f.Id))
                .ToListAsync(cancellationToken);

            foreach (var uf in uploadFiles)
            {
                // Один и тот же OwnerId хранится в Uploaders ровно один раз (см. AddUploaderToFile),
                // поэтому даже если у нас несколько entry на один UploadFile — снимаем owner-а единожды.
                uf.Uploaders.Remove(ownerId);
            }

            // Декремент и для превью-файлов, привязанных к удаляемым оригиналам.
            var previewFileIds = await _context.FilePreviews
                .AsNoTracking()
                .Where(p => fileIds.Contains(p.OriginalFileId))
                .Select(p => p.PreviewFileId)
                .Distinct()
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

        _storage.RemoveFileEntries(entries);
        _storage.RemoveDirectories(subtree);

        await _storage.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Удалена папка {DirectoryId} рекурсивно (директорий: {DirCount}, файлов: {FileCount}, Owner: {OwnerId})",
            root.Id, subtree.Count, entries.Count, ownerId);

        return new CloudEmpty();
    }
}
