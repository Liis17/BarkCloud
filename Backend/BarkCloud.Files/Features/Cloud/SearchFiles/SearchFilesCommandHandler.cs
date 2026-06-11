using BarkCloud.Files.Helpers;
using BarkCloud.Files.Mapping;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.SearchFiles;

/// <summary>
/// Поиск файлов пользователя по подстроке имени (по всему облаку, независимо от папок).
/// Возвращает обогащённые <see cref="FileEntryDetailed"/> (запись + файл с превью/URL),
/// как <c>ListDirectoryDetailed</c>, с cursor-пагинацией.
/// </summary>
public class SearchFilesCommandHandler : IRequestHandler<SearchFilesCommand, SearchFilesResponse>
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private readonly ICloudHierarchyStorage _storage;
    private readonly IUploadedFilesStorage _uploadedFiles;
    private readonly UserContext _userContext;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;

    public SearchFilesCommandHandler(
        ICloudHierarchyStorage storage,
        IUploadedFilesStorage uploadedFiles,
        UserContext userContext,
        RunSettings runSettings,
        IConfiguration configuration)
    {
        _storage = storage;
        _uploadedFiles = uploadedFiles;
        _userContext = userContext;
        _runSettings = runSettings;
        _configuration = configuration;
    }

    public async Task<SearchFilesResponse> Handle(SearchFilesCommand request, CancellationToken cancellationToken)
    {
        var query = request.Query?.Trim() ?? string.Empty;
        if (query.Length == 0)
            return new SearchFilesResponse();

        var ownerId = _userContext.UserId;
        var limit = request.Limit <= 0 ? DefaultLimit : Math.Min(request.Limit, MaxLimit);

        var entries = await _storage.SearchFileEntriesPage(
            ownerId, query, request.CursorCreatedAt, request.CursorEntryId, limit, request.KindFilter, cancellationToken);

        var hasMore = entries.Count > limit;
        var page = hasMore ? entries.Take(limit).ToList() : entries;

        var response = new SearchFilesResponse();
        if (page.Count == 0)
            return response;

        var fileIds = page.Select(e => e.FileId).Distinct().ToList();
        var filesById = (await _uploadedFiles.GetFiles(fileIds)).ToDictionary(f => f.Id);
        var previewsByOriginal = await _uploadedFiles.GetPreviewsForFiles(fileIds, cancellationToken);
        var baseUrl = FileUrlHelper.GetPublicBaseUrl(_configuration, _runSettings);

        foreach (var e in page)
        {
            var entryInfo = new FileEntryInfo
            {
                Id = e.Id.ToString(),
                DirectoryId = e.DirectoryId == CloudHierarchyStorage.RootDirectoryId ? string.Empty : e.DirectoryId.ToString(),
                FileId = e.FileId.ToString(),
                Name = e.Name,
                CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(e.CreatedAt, DateTimeKind.Utc))
            };

            UploadFileInfo? fileInfo = null;
            if (filesById.TryGetValue(e.FileId, out var file))
            {
                previewsByOriginal.TryGetValue(file.Id, out var previews);
                fileInfo = file.ToGrpc(baseUrl, previews);
            }

            response.Files.Add(new FileEntryDetailed
            {
                Entry = entryInfo,
                File = fileInfo ?? new UploadFileInfo { Id = e.FileId.ToString() }
            });
        }

        if (hasMore)
        {
            var last = page[^1];
            response.NextCursorCreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(last.CreatedAt, DateTimeKind.Utc));
            response.NextCursorEntryId = last.Id.ToString();
        }

        return response;
    }
}
