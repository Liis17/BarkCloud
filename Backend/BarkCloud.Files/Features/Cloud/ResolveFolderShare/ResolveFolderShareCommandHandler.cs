using BarkCloud.Files.Helpers;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ResolveFolderShare;

/// <summary>
/// Анонимный резолв публичной папки по токену. Возвращает динамический листинг текущей папки
/// (подпапки + файлы с публичными temp-URL скачивания и URL превью). Навигация по поддереву —
/// через <see cref="ResolveFolderShareCommand.Dir"/> (валидируется принадлежностью к поддереву).
/// </summary>
public class ResolveFolderShareCommandHandler : IRequestHandler<ResolveFolderShareCommand, ResolveFolderShareResponse>
{
    private readonly IFolderShareStorage _folderShares;
    private readonly ICloudHierarchyStorage _hierarchy;
    private readonly IUploadedFilesStorage _filesStorage;
    private readonly ITempFilesStorage _tempFilesStorage;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ResolveFolderShareCommandHandler> _logger;

    public ResolveFolderShareCommandHandler(
        IFolderShareStorage folderShares,
        ICloudHierarchyStorage hierarchy,
        IUploadedFilesStorage filesStorage,
        ITempFilesStorage tempFilesStorage,
        RunSettings runSettings,
        IConfiguration configuration,
        ILogger<ResolveFolderShareCommandHandler> logger)
    {
        _folderShares = folderShares;
        _hierarchy = hierarchy;
        _filesStorage = filesStorage;
        _tempFilesStorage = tempFilesStorage;
        _runSettings = runSettings;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ResolveFolderShareResponse> Handle(ResolveFolderShareCommand request, CancellationToken cancellationToken)
    {
        var share = await _folderShares.GetByToken(request.Token, cancellationToken);
        if (share is null)
            return new ResolveFolderShareResponse { Found = false };

        var ownerId = share.OwnerId;

        // Корень расшаренной папки мог быть удалён — отдадим 404.
        var root = await _hierarchy.GetDirectoryAsNoTracking(share.DirectoryId, cancellationToken);
        if (root is null)
            return new ResolveFolderShareResponse { Found = false };

        // Поддерево расшаренной папки (рекурсивно). Нужно для валидации dir и навигации.
        var subtree = await _hierarchy.GetSubtree(ownerId, root.Id, cancellationToken);
        var subtreeById = subtree.ToDictionary(d => d.Id);

        Domain.CloudDirectory current = root;
        if (!string.IsNullOrWhiteSpace(request.Dir))
        {
            if (!Guid.TryParse(request.Dir, out var dirId) || !subtreeById.TryGetValue(dirId, out var found))
                return new ResolveFolderShareResponse { Found = false };
            current = found;
        }
        else
        {
            // Открытие корня публичной папки — считаем переход.
            await _folderShares.IncrementClicks(share.Id, cancellationToken);
        }

        var subdirs = await _hierarchy.ListSubdirectories(ownerId, current.Id, cancellationToken);
        var entries = await _hierarchy.ListFilesInDirectory(ownerId, current.Id, cancellationToken);

        var response = new ResolveFolderShareResponse
        {
            Found = true,
            FolderName = root.Name,
            CurrentDir = current.Id == root.Id ? string.Empty : current.Id.ToString(),
            CurrentName = current.Name
        };

        foreach (var d in subdirs.OrderBy(x => x.Name))
            response.Subdirs.Add(new PublicDirEntry { Id = d.Id.ToString(), Name = d.Name });

        if (entries.Count > 0)
        {
            var baseUrl = FileUrlHelper.GetPublicBaseUrl(_configuration, _runSettings);
            var fileIds = entries.Select(e => e.FileId).Distinct().ToList();
            var files = (await _filesStorage.GetFiles(fileIds)).ToDictionary(f => f.Id);
            var previewsByFile = await _filesStorage.GetPreviewsForFiles(fileIds, cancellationToken);

            // Прямой /download/{fileId} для CloudFile запрещён — выдаём временные ссылки на оригиналы.
            var tempFiles = await _tempFilesStorage.CreateTempFilesBatchAsync(fileIds, cancellationToken);
            var tempByOriginal = tempFiles
                .GroupBy(t => t.OriginalFileId)
                .ToDictionary(g => g.Key, g => g.First().Id);

            foreach (var e in entries)
            {
                files.TryGetValue(e.FileId, out var file);

                var downloadUrl = tempByOriginal.TryGetValue(e.FileId, out var tempId)
                    ? FileUrlHelper.GenerateDownloadUrl(baseUrl, tempId)
                    : string.Empty;

                var previewUrl = string.Empty;
                if (previewsByFile.TryGetValue(e.FileId, out var previews) && previews.Count > 0)
                    previewUrl = FileUrlHelper.GenerateDownloadUrl(baseUrl, previews.OrderByDescending(p => p.TargetWidth).First().PreviewFileId);

                response.Files.Add(new PublicFileEntry
                {
                    FileId = e.FileId.ToString(),
                    Name = e.Name,
                    MediaKind = file is null ? MediaKind.Other : (MediaKind)(int)file.MediaKind,
                    DownloadUrl = downloadUrl,
                    PreviewUrl = previewUrl,
                    FileSize = file?.Size ?? 0,
                    ImageWidth = file?.ImageWidth ?? 0,
                    ImageHeight = file?.ImageHeight ?? 0
                });
            }
        }

        _logger.LogInformation("Резолв публичной папки {ShareId} dir={Dir}: подпапок {Subdirs}, файлов {Files}",
            share.Id, current.Id, response.Subdirs.Count, response.Files.Count);

        return response;
    }
}
