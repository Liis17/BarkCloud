using BarkCloud.Files.Helpers;
using BarkCloud.Files.Mapping;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

using Microsoft.EntityFrameworkCore;

using DomainMediaKind = BarkCloud.Files.Domain.MediaKind;

namespace BarkCloud.Files.Features.Cloud.ListUserMedia;

/// <summary>
/// Листинг медиа пользователя (фото / видео) с cursor-пагинацией и превью.
/// В отличие от устаревшего ListUserImages фильтрует по явному <see cref="DomainMediaKind"/>
/// и исключает превью-блобы (PreviewFileId из FilePreviews), чтобы они не протекали в галерею.
/// </summary>
public class ListUserMediaCommandHandler : IRequestHandler<ListUserMediaCommand, ListUserMediaResponse>
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;
    private const int MaxEntryNames = 5;

    private readonly FilesContext _context;
    private readonly IUploadedFilesStorage _uploadedFiles;
    private readonly UserContext _userContext;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ListUserMediaCommandHandler> _logger;

    public ListUserMediaCommandHandler(
        FilesContext context,
        IUploadedFilesStorage uploadedFiles,
        UserContext userContext,
        RunSettings runSettings,
        IConfiguration configuration,
        ILogger<ListUserMediaCommandHandler> logger)
    {
        _context = context;
        _uploadedFiles = uploadedFiles;
        _userContext = userContext;
        _runSettings = runSettings;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ListUserMediaResponse> Handle(ListUserMediaCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var limit = request.Limit <= 0 ? DefaultLimit : Math.Min(request.Limit, MaxLimit);
        var kind = request.Kind;

        // Файлы владельца нужного медиа-типа, исключая превью-блобы и «эффективно удалённые»
        // (все записи владельца на файл — в корзине; файлы без записи или с живой записью остаются).
        var query = _context.UploadedFiles
            .AsNoTracking()
            .Where(f => f.Uploaders.Contains(ownerId)
                        && f.Type == Domain.UploadFileType.CloudFile
                        && f.MediaKind == kind
                        && !_context.FilePreviews.Any(p => p.PreviewFileId == f.Id)
                        && !(_context.CloudFileEntries.Any(e => e.OwnerId == ownerId && e.FileId == f.Id && e.IsDeleted)
                             && !_context.CloudFileEntries.Any(e => e.OwnerId == ownerId && e.FileId == f.Id && !e.IsDeleted)));

        if (request.CursorCreatedAt.HasValue && request.CursorFileId.HasValue)
        {
            var cursorCreatedAt = DateTime.SpecifyKind(request.CursorCreatedAt.Value, DateTimeKind.Utc);
            var cursorFileId = request.CursorFileId.Value;

            query = query.Where(f =>
                f.CreatedAt < cursorCreatedAt
                || (f.CreatedAt == cursorCreatedAt && f.Id.ToString().CompareTo(cursorFileId.ToString()) < 0));
        }

        var page = await query
            .OrderByDescending(f => f.CreatedAt)
            .ThenByDescending(f => f.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        var hasMore = page.Count > limit;
        if (hasMore)
            page.RemoveAt(page.Count - 1);

        var response = new ListUserMediaResponse();
        if (page.Count == 0)
            return response;

        var pageFileIds = page.Select(f => f.Id).ToList();

        var previewsByOriginal = await _uploadedFiles.GetPreviewsForFiles(pageFileIds, cancellationToken);
        var baseUrl = FileUrlHelper.GetPublicBaseUrl(_configuration, _runSettings);

        var entries = await _context.CloudFileEntries
            .AsNoTracking()
            .Where(e => e.OwnerId == ownerId && pageFileIds.Contains(e.FileId) && !e.IsDeleted)
            .Select(e => new { e.Id, e.FileId, e.Name, e.CreatedAt })
            .ToListAsync(cancellationToken);

        var entriesByFileId = entries
            .GroupBy(e => e.FileId)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    Count = g.Count(),
                    Names = g.OrderByDescending(x => x.CreatedAt)
                            .Take(MaxEntryNames)
                            .Select(x => x.Name)
                            .ToList(),
                    Ids = g.OrderByDescending(x => x.CreatedAt)
                            .Select(x => x.Id.ToString())
                            .ToList()
                });

        foreach (var file in page)
        {
            previewsByOriginal.TryGetValue(file.Id, out var previews);
            var item = new UserImageItem
            {
                File = file.ToGrpc(baseUrl, previews)
            };

            if (entriesByFileId.TryGetValue(file.Id, out var meta))
            {
                item.EntriesCount = meta.Count;
                item.EntryNames.AddRange(meta.Names);
                item.EntryIds.AddRange(meta.Ids);
            }

            response.Items.Add(item);
        }

        if (hasMore)
        {
            var last = page[^1];
            response.NextCursorCreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(last.CreatedAt, DateTimeKind.Utc));
            response.NextCursorFileId = last.Id.ToString();
        }

        _logger.LogDebug(
            "ListUserMedia: owner={Owner} kind={Kind} returned={Count} hasMore={HasMore}",
            ownerId, kind, page.Count, hasMore);

        return response;
    }
}
