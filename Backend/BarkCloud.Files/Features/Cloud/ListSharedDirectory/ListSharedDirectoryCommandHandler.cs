using BarkCloud.Files.Helpers;
using BarkCloud.Files.Persistence;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ListSharedDirectory;

/// <summary>
/// Листинг папки, доступной получателю через грант на папку. Навигация по поддереву:
/// <paramref name="ListSharedDirectoryCommand.DirectoryId"/> валидируется принадлежностью к поддереву
/// какого-либо гранта получателя. Файлы отдаются с публичными temp-URL и URL превью.
/// </summary>
public class ListSharedDirectoryCommandHandler : IRequestHandler<ListSharedDirectoryCommand, ListSharedDirectoryResponse>
{
    private readonly FolderGrantAccessService _access;
    private readonly ICloudHierarchyStorage _hierarchy;
    private readonly IUploadedFilesStorage _filesStorage;
    private readonly ITempFilesStorage _tempFilesStorage;
    private readonly UserContext _userContext;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;

    public ListSharedDirectoryCommandHandler(
        FolderGrantAccessService access,
        ICloudHierarchyStorage hierarchy,
        IUploadedFilesStorage filesStorage,
        ITempFilesStorage tempFilesStorage,
        UserContext userContext,
        RunSettings runSettings,
        IConfiguration configuration)
    {
        _access = access;
        _hierarchy = hierarchy;
        _filesStorage = filesStorage;
        _tempFilesStorage = tempFilesStorage;
        _userContext = userContext;
        _runSettings = runSettings;
        _configuration = configuration;
    }

    public async Task<ListSharedDirectoryResponse> Handle(ListSharedDirectoryCommand request, CancellationToken cancellationToken)
    {
        var recipientId = _userContext.UserId;

        var ownerId = await _access.ResolveAccessibleDirectoryOwner(recipientId, request.DirectoryId, cancellationToken);
        if (ownerId is null)
            return new ListSharedDirectoryResponse { Found = false };

        var dir = await _hierarchy.GetDirectoryAsNoTracking(request.DirectoryId, cancellationToken);
        if (dir is null)
            return new ListSharedDirectoryResponse { Found = false };

        var subdirs = await _hierarchy.ListSubdirectories(ownerId.Value, request.DirectoryId, cancellationToken);
        var entries = await _hierarchy.ListFilesInDirectory(ownerId.Value, request.DirectoryId, cancellationToken);

        var response = new ListSharedDirectoryResponse
        {
            Found = true,
            DirectoryId = dir.Id.ToString(),
            Name = dir.Name
        };

        foreach (var d in subdirs.OrderBy(x => x.Name))
            response.Subdirs.Add(new PublicDirEntry { Id = d.Id.ToString(), Name = d.Name });

        if (entries.Count > 0)
        {
            var baseUrl = FileUrlHelper.GetPublicBaseUrl(_configuration, _runSettings);
            var fileIds = entries.Select(e => e.FileId).Distinct().ToList();
            var files = (await _filesStorage.GetFiles(fileIds)).ToDictionary(f => f.Id);
            var previewsByFile = await _filesStorage.GetPreviewsForFiles(fileIds, cancellationToken);

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

        return response;
    }
}
