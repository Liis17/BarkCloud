using BarkCloud.Files.Domain;
using BarkCloud.Files.Helpers;
using BarkCloud.Files.Mapping;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Microsoft.EntityFrameworkCore;

using DomainMediaKind = BarkCloud.Files.Domain.MediaKind;
using ProtoMediaKind = BarkCloud.Proto.Files.MediaKind;
using DomainUploadFileType = BarkCloud.Files.Domain.UploadFileType;

namespace BarkCloud.Files.Services;

/// <summary>
/// Личный поиск файлового сервиса. Все данные выбираются в контексте текущего пользователя;
/// алиасы и теги никогда не смешиваются с данными получателей shared-доступа.
/// </summary>
public partial class UnifiedSearchService
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;
    private const int MaxQueryLength = 200;
    private const int MaxAliasLength = 120;
    private const int MaxTags = 20;
    private const int MaxTagLength = 50;

    private readonly FilesContext _context;
    private readonly UserContext _userContext;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;

    public UnifiedSearchService(
        FilesContext context,
        UserContext userContext,
        RunSettings runSettings,
        IConfiguration configuration)
    {
        _context = context;
        _userContext = userContext;
        _runSettings = runSettings;
        _configuration = configuration;
    }


    public async Task<SearchResponse> Search(SearchRequest request, CancellationToken cancellationToken)
    {
        var query = SearchText.Normalize(request.Query);
        if (query.Length > MaxQueryLength)
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"Поисковый запрос не длиннее {MaxQueryLength} символов"));

        var pages = request.Pages.Count == 0
            ? DefaultPages()
            : request.Pages.Where(p => p.Section != SearchSection.Unspecified).ToList();
        var response = new SearchResponse();
        if (!SearchText.IsSearchableQuery(query))
        {
            foreach (var page in pages)
                response.Sections.Add(new SearchSectionResult { Section = page.Section });
            return response;
        }

        var terms = new SearchTerms(query);
        var requests = pages
            .Select(page => (Page: page, Limit: page.Limit <= 0 ? DefaultLimit : Math.Min(page.Limit, MaxLimit), Cursor: SearchCursor.Parse(page.Cursor)))
            .ToList();

        // Каждая секция запрашивает у БД только свою страницу (limit + 1 строка на источник).
        var loaded = new List<List<PendingHit>>();
        foreach (var (page, limit, cursor) in requests)
            loaded.Add(await LoadSection(page.Section, new SectionQuery(terms, cursor, limit + 1, null, null), cancellationToken));

        // Связанные данные грузятся один раз и только для файлов, попавших на страницы.
        var fileIds = new HashSet<Guid>();
        for (var i = 0; i < requests.Count; i++)
            fileIds.UnionWith(loaded[i].Take(requests[i].Limit).Where(x => x.FileId.HasValue).Select(x => x.FileId!.Value));
        var enrichment = await LoadEnrichment(fileIds, cancellationToken);

        for (var i = 0; i < requests.Count; i++)
        {
            var (page, limit, _) = requests[i];
            var hits = loaded[i];
            var hasMore = hits.Count > limit;
            var visible = hits.Take(limit).ToList();
            var result = new SearchSectionResult { Section = page.Section, HasMore = hasMore };
            result.Hits.AddRange(visible.Select(x => BuildHit(x, enrichment)));
            if (hasMore && visible.Count > 0)
                result.NextCursor = SearchCursor.Encode(visible[^1].Rank, visible[^1].Similarity, visible[^1].SortAt, visible[^1].Kind.ToString(), visible[^1].Id);
            response.Sections.Add(result);
        }

        return response;
    }

    public async Task<SearchHit> ResolveHit(SearchHitReference request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Id))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Не указан идентификатор результата"));

        var section = request.Kind switch
        {
            SearchHitKind.Photo => SearchSection.Photos,
            SearchHitKind.Video => SearchSection.Videos,
            SearchHitKind.File => SearchSection.Files,
            SearchHitKind.Track => SearchSection.Tracks,
            SearchHitKind.Album => SearchSection.Albums,
            SearchHitKind.Playlist => SearchSection.Playlists,
            SearchHitKind.Folder or SearchHitKind.DynamicFolder => SearchSection.Folders,
            SearchHitKind.SharedFile or SearchHitKind.SharedFolder or SearchHitKind.SharedPlaylist => SearchSection.Shared,
            SearchHitKind.Trash => SearchSection.Trash,
            _ => throw new RpcException(new Status(StatusCode.InvalidArgument, "Неизвестный тип результата"))
        };

        // Id хита своего файла — id записи в облаке, а галереи (фото/видео/музыка) открывают результат по id файла.
        var hits = await LoadSection(section, new SectionQuery(new SearchTerms(string.Empty), null, 2, request.Id, request.Kind), cancellationToken);
        var hit = hits.FirstOrDefault(x => x.Kind == request.Kind)
                  ?? throw new RpcException(new Status(StatusCode.NotFound, "Результат больше недоступен"));
        var enrichment = await LoadEnrichment(hit.FileId.HasValue ? [hit.FileId.Value] : [], cancellationToken);
        return BuildHit(hit, enrichment);
    }

    public async Task<FileSearchMetadata> GetFileSearchMetadata(Guid fileId, CancellationToken cancellationToken)
    {
        await RequireOwnedFile(fileId, cancellationToken);
        var ownerId = _userContext.UserId;
        var alias = await _context.FileSearchAliases.AsNoTracking()
            .Where(x => x.OwnerId == ownerId && x.FileId == fileId)
            .Select(x => x.Value)
            .FirstOrDefaultAsync(cancellationToken);
        var tags = await _context.FileTags.AsNoTracking()
            .Where(x => x.OwnerId == ownerId && x.FileId == fileId)
            .OrderBy(x => x.Value)
            .Select(x => x.Value)
            .ToListAsync(cancellationToken);

        var result = new FileSearchMetadata { Alias = alias ?? string.Empty };
        result.Tags.AddRange(tags);
        return result;
    }

    public async Task<FileSearchMetadata> ReplaceFileSearchMetadata(Guid fileId, string? aliasRaw, IEnumerable<string> tagValues, CancellationToken cancellationToken)
    {
        await RequireOwnedFile(fileId, cancellationToken);
        var ownerId = _userContext.UserId;
        var alias = SearchText.CollapseWhitespace(aliasRaw);
        if (alias.Length > MaxAliasLength)
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"Алиас не длиннее {MaxAliasLength} символов"));

        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in tagValues)
        {
            var value = SearchText.CollapseWhitespace(raw);
            if (value.Length == 0)
                continue;
            if (value.Length > MaxTagLength)
                throw new RpcException(new Status(StatusCode.InvalidArgument, $"Тег не длиннее {MaxTagLength} символов"));
            tags.TryAdd(SearchText.Normalize(value), value);
        }
        if (tags.Count > MaxTags)
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"Можно указать не больше {MaxTags} тегов"));

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        var existingAlias = await _context.FileSearchAliases
            .FirstOrDefaultAsync(x => x.OwnerId == ownerId && x.FileId == fileId, cancellationToken);
        if (alias.Length == 0)
        {
            if (existingAlias is not null)
                _context.FileSearchAliases.Remove(existingAlias);
        }
        else if (existingAlias is null)
        {
            _context.FileSearchAliases.Add(new FileSearchAlias
            {
                OwnerId = ownerId,
                FileId = fileId,
                Value = alias,
                NormalizedValue = SearchText.Normalize(alias),
                UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            existingAlias.Value = alias;
            existingAlias.NormalizedValue = SearchText.Normalize(alias);
            existingAlias.UpdatedAt = DateTime.UtcNow;
        }

        await _context.FileTags.Where(x => x.OwnerId == ownerId && x.FileId == fileId).ExecuteDeleteAsync(cancellationToken);
        var now = DateTime.UtcNow;
        foreach (var (normalized, value) in tags)
        {
            _context.FileTags.Add(new FileTag
            {
                OwnerId = ownerId,
                FileId = fileId,
                Value = value,
                NormalizedValue = normalized,
                CreatedAt = now
            });
        }
        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var result = new FileSearchMetadata { Alias = alias };
        result.Tags.AddRange(tags.Values.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase));
        return result;
    }

    /// <summary>Данные хита, найденного в БД: порядок выдачи известен сразу, <see cref="Build"/> вызывается только для хитов страницы.</summary>
    private sealed record PendingHit(
        SearchHitKind Kind, string Id, int Rank, double Similarity, DateTime SortAt, Guid? FileId, string MatchField, string MatchValue,
        Func<FileEnrichment, SearchHit> Build);

    /// <summary>Связанные данные файлов страницы (не каталога).</summary>
    private sealed record FileEnrichment(
        Dictionary<Guid, FileMetadata> Metadata,
        HashSet<Guid> FavoriteIds,
        Dictionary<Guid, List<FilePreview>> Previews,
        Dictionary<Guid, FilePlaceholder> Placeholders,
        string PublicBaseUrl);

    private async Task<FileEnrichment> LoadEnrichment(ICollection<Guid> fileIds, CancellationToken cancellationToken)
    {
        var ids = fileIds.ToList();
        var metadata = ids.Count == 0 ? new Dictionary<Guid, FileMetadata>() : await _context.FileMetadata.AsNoTracking()
            .Where(x => ids.Contains(x.FileId))
            .ToDictionaryAsync(x => x.FileId, cancellationToken);
        var ownerId = _userContext.UserId;
        var favoriteIds = ids.Count == 0 ? new HashSet<Guid>() : (await _context.FavoriteFiles.AsNoTracking()
            .Where(x => x.OwnerId == ownerId && ids.Contains(x.FileId))
            .Select(x => x.FileId)
            .ToListAsync(cancellationToken)).ToHashSet();
        var previews = ids.Count == 0 ? new Dictionary<Guid, List<FilePreview>>() : await _context.FilePreviews.AsNoTracking()
            .Where(x => ids.Contains(x.OriginalFileId) && x.TargetWidth >= 0)
            .GroupBy(x => x.OriginalFileId)
            .ToDictionaryAsync(x => x.Key, x => x.OrderBy(p => p.TargetWidth).ToList(), cancellationToken);

        var placeholders = ids.Count == 0 ? new Dictionary<Guid, FilePlaceholder>() : await _context.FilePlaceholders.AsNoTracking()
            .Where(x => ids.Contains(x.FileId))
            .ToDictionaryAsync(x => x.FileId, cancellationToken);

        return new FileEnrichment(metadata, favoriteIds, previews, placeholders, FileUrlHelper.GetPublicBaseUrl(_configuration, _runSettings));
    }

    private static SearchHit BuildHit(PendingHit pending, FileEnrichment data)
    {
        var hit = pending.Build(data);
        hit.MatchField = pending.MatchField;
        hit.MatchValue = pending.MatchValue;
        if (pending.FileId is { } fileId && hit.MediaKind is ProtoMediaKind.Photo or ProtoMediaKind.Video
            && data.Placeholders.TryGetValue(fileId, out var placeholder))
            hit.Placeholder = placeholder.ToGrpc();
        return hit;
    }

    private static SearchHit OwnedFileHit(UploadFile file, CloudFileEntry? entry, bool trash, SearchHitKind kind, FileEnrichment data)
    {
        var title = entry?.Name ?? file.Filename ?? "Файл";
        data.Metadata.TryGetValue(file.Id, out var meta);
        var subtitle = file.MediaKind == DomainMediaKind.Audio
            ? string.Join(" · ", new[] { meta?.AudioArtist, meta?.AudioAlbum }.Where(x => !string.IsNullOrWhiteSpace(x)))
            : trash ? "В корзине" : FileSubtitle(file.MediaKind, meta);
        var id = trash ? entry!.Id.ToString() : (entry?.Id.ToString() ?? file.Id.ToString());
        return CreateHit(kind, id, title, subtitle, file.Id, data.FavoriteIds.Contains(file.Id), entry?.Id.ToString() ?? string.Empty,
            trash ? entry!.DeletedAt ?? entry.CreatedAt : file.CreatedAt, file.MediaKind, file.Size, PreviewUrl(data, file.Id));
    }

    /// <summary>Хит без данных файла (альбом, плейлист, папка, shared); порядок задан рангом из БД.</summary>
    private static PendingHit SimpleHit(
        SearchHitKind kind, string id, int rank, double similarity, DateTime sortAt, string title, string? subtitle, Guid? targetId,
        string matchField, string matchValue)
        => new(kind, id, rank, similarity, sortAt, null, matchField, matchValue,
            _ => CreateHit(kind, id, title, subtitle, targetId, false, string.Empty, sortAt, DomainMediaKind.Other, 0, string.Empty));

    private static SearchHit CreateHit(
        SearchHitKind kind, string id, string title, string? subtitle, Guid? fileId, bool favorite, string entryId, DateTime sortAt,
        DomainMediaKind mediaKind, long size, string previewUrl)
    {
        return new SearchHit
        {
            Kind = kind,
            Id = id,
            FileId = fileId?.ToString() ?? string.Empty,
            EntryId = entryId,
            Title = title,
            Subtitle = subtitle ?? string.Empty,
            PreviewUrl = previewUrl,
            MediaKind = (ProtoMediaKind)(int)mediaKind,
            Favorite = favorite,
            MatchField = string.Empty,
            MatchValue = string.Empty,
            CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(sortAt, DateTimeKind.Utc)),
            Size = size
        };
    }


    private static string FileSubtitle(DomainMediaKind kind, FileMetadata? metadata) => kind switch
    {
        DomainMediaKind.Document when !string.IsNullOrWhiteSpace(metadata?.DocumentTitle) => metadata.DocumentTitle!,
        DomainMediaKind.Document => "Файл",
        DomainMediaKind.Other => "Файл",
        _ => string.Empty
    };

    private static string PreviewUrl(FileEnrichment data, Guid fileId)
    {
        if (!data.Previews.TryGetValue(fileId, out var previews) || previews.Count == 0)
            return string.Empty;
        var preview = previews.FirstOrDefault(x => x.TargetWidth == 512) ?? previews[^1];
        return FileUrlHelper.GenerateDownloadUrl(data.PublicBaseUrl, preview.PreviewFileId);
    }

    private async Task RequireOwnedFile(Guid fileId, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var exists = await _context.UploadedFiles.AsNoTracking().WhereReady()
            .AnyAsync(x => x.Id == fileId
                           && x.Uploaders.Contains(ownerId), cancellationToken);
        if (!exists)
            throw new RpcException(new Status(StatusCode.NotFound, "Файл не найден"));
    }

    private static List<SearchSectionPage> DefaultPages() =>
    [
        new() { Section = SearchSection.Photos, Limit = 12 },
        new() { Section = SearchSection.Videos, Limit = 12 },
        new() { Section = SearchSection.Files, Limit = 20 },
        new() { Section = SearchSection.Tracks, Limit = 20 },
        new() { Section = SearchSection.Albums, Limit = 12 },
        new() { Section = SearchSection.Playlists, Limit = 12 },
        new() { Section = SearchSection.Folders, Limit = 20 },
        new() { Section = SearchSection.Shared, Limit = 20 },
        new() { Section = SearchSection.Trash, Limit = 20 },
    ];
}
