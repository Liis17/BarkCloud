using BarkCloud.Files.Domain;
using BarkCloud.Files.Helpers;
using BarkCloud.Files.Mapping;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

using Microsoft.EntityFrameworkCore;

using DomainUploadFileType = BarkCloud.Files.Domain.UploadFileType;

namespace BarkCloud.Files.Features.Cloud.ListUserImages;

public class ListUserImagesCommandHandler : IRequestHandler<ListUserImagesCommand, ListUserImagesResponse>
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;
    private const int MaxEntryNames = 5;

    private readonly FilesContext _context;
    private readonly UploadedFilesStorage _uploadedFiles;
    private readonly UserContext _userContext;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ListUserImagesCommandHandler> _logger;

    public ListUserImagesCommandHandler(
        FilesContext context,
        UploadedFilesStorage uploadedFiles,
        UserContext userContext,
        RunSettings runSettings,
        IConfiguration configuration,
        ILogger<ListUserImagesCommandHandler> logger)
    {
        _context = context;
        _uploadedFiles = uploadedFiles;
        _userContext = userContext;
        _runSettings = runSettings;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ListUserImagesResponse> Handle(ListUserImagesCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var limit = request.Limit <= 0 ? DefaultLimit : Math.Min(request.Limit, MaxLimit);

        // Базовый предикат: файлы пользователя в облаке, прошедшие фильтр «изображение».
        // Фильтр изображения — ImageWidth>0 ИЛИ имя оканчивается на одно из известных расширений
        // (через LOWER + EndsWith, чтобы работало кросс-провайдерно).
        var query = _context.UploadedFiles
            .AsNoTracking()
            .Where(f => f.Uploaders.Contains(ownerId)
                        && f.Type == DomainUploadFileType.CloudFile
                        // Превью-блобы (PreviewFileId из FilePreviews) не должны протекать в галерею.
                        && !_context.FilePreviews.Any(p => p.PreviewFileId == f.Id)
                        && (
                            (f.ImageWidth != null && f.ImageWidth > 0)
                            || (f.Filename != null && (
                                    f.Filename.ToLower().EndsWith(".jpg")
                                 || f.Filename.ToLower().EndsWith(".jpeg")
                                 || f.Filename.ToLower().EndsWith(".png")
                                 || f.Filename.ToLower().EndsWith(".gif")
                                 || f.Filename.ToLower().EndsWith(".webp")
                                 || f.Filename.ToLower().EndsWith(".heic")
                                 || f.Filename.ToLower().EndsWith(".heif")
                                 || f.Filename.ToLower().EndsWith(".bmp")
                                 || f.Filename.ToLower().EndsWith(".tiff")
                                 || f.Filename.ToLower().EndsWith(".tif")))
                        ));

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

        var response = new ListUserImagesResponse();
        if (page.Count == 0)
            return response;

        var pageFileIds = page.Select(f => f.Id).ToList();

        // Подгружаем превью для всех файлов страницы одним запросом.
        var previewsByOriginal = await _uploadedFiles.GetPreviewsForFiles(pageFileIds, cancellationToken);
        var baseUrl = FileUrlHelper.GetPublicBaseUrl(_configuration, _runSettings);

        // Считаем количество и имена FileEntry для каждого FileId пачкой.
        // Делаем в памяти после быстрого выбора всех записей — это надёжнее, чем GroupBy+Take на стороне БД,
        // и стоимость низкая: список ограничен limit (≤200) копий каждого файла на одного владельца, что на практике небольшие десятки.
        var entries = await _context.CloudFileEntries
            .AsNoTracking()
            .Where(e => e.OwnerId == ownerId && pageFileIds.Contains(e.FileId))
            .Select(e => new { e.FileId, e.Name, e.CreatedAt })
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
            "ListUserImages: owner={Owner} returned={Count} hasMore={HasMore}",
            ownerId, page.Count, hasMore);

        return response;
    }
}
