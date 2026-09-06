using System.Text;
using System.Globalization;

using BarkCloud.Files.Domain;
using BarkCloud.Files.Helpers;
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
public class UnifiedSearchService
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

        var data = await LoadData(query, cancellationToken);
        foreach (var page in pages)
        {
            var hits = BuildHits(page.Section, data, query);
            response.Sections.Add(Page(page.Section, hits, page.Limit, page.Cursor));
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

        var data = await LoadData(null, cancellationToken);
        var hit = BuildHits(section, data, string.Empty)
            .Select(x => x.Hit)
            .FirstOrDefault(x => x.Kind == request.Kind && x.Id == request.Id);
        return hit ?? throw new RpcException(new Status(StatusCode.NotFound, "Результат больше недоступен"));
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

    private async Task<SearchData> LoadData(string? query, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var previewFileIds = _context.FilePreviews.AsNoTracking().Select(x => x.PreviewFileId);
        var fileQuery = _context.UploadedFiles.AsNoTracking()
            .Where(x => x.Uploaders.Contains(ownerId) && x.Type == DomainUploadFileType.CloudFile && !previewFileIds.Contains(x.Id))
            .AsQueryable();
        if (!string.IsNullOrEmpty(query))
        {
            var pattern = LikeContainsPattern(query);
            var matchingFileIds = _context.CloudFileEntries.AsNoTracking()
                    .Where(x => x.OwnerId == ownerId && (EF.Functions.ILike(x.Name, pattern, "\\")
                        || query.Length >= 4 && EF.Functions.TrigramsWordSimilarity(x.Name, query) >= .45d))
                    .Select(x => x.FileId)
                .Union(_context.FileSearchAliases.AsNoTracking()
                    .Where(x => x.OwnerId == ownerId && (EF.Functions.ILike(x.NormalizedValue, pattern, "\\")
                        || query.Length >= 4 && EF.Functions.TrigramsWordSimilarity(x.NormalizedValue, query) >= .45d))
                    .Select(x => x.FileId))
                .Union(_context.FileTags.AsNoTracking()
                    .Where(x => x.OwnerId == ownerId && (EF.Functions.ILike(x.NormalizedValue, pattern, "\\")
                        || query.Length >= 4 && EF.Functions.TrigramsWordSimilarity(x.NormalizedValue, query) >= .45d))
                    .Select(x => x.FileId))
                .Union(_context.FileMetadata.AsNoTracking()
                    .Where(x => x.DocumentTitle != null && (EF.Functions.ILike(x.DocumentTitle, pattern, "\\") || query.Length >= 4 && EF.Functions.TrigramsWordSimilarity(x.DocumentTitle, query) >= .45d)
                        || x.DocumentAuthor != null && (EF.Functions.ILike(x.DocumentAuthor, pattern, "\\") || query.Length >= 4 && EF.Functions.TrigramsWordSimilarity(x.DocumentAuthor, query) >= .45d)
                        || x.DocumentSubject != null && (EF.Functions.ILike(x.DocumentSubject, pattern, "\\") || query.Length >= 4 && EF.Functions.TrigramsWordSimilarity(x.DocumentSubject, query) >= .45d)
                        || x.AudioTitle != null && (EF.Functions.ILike(x.AudioTitle, pattern, "\\") || query.Length >= 4 && EF.Functions.TrigramsWordSimilarity(x.AudioTitle, query) >= .45d)
                        || x.AudioArtist != null && (EF.Functions.ILike(x.AudioArtist, pattern, "\\") || query.Length >= 4 && EF.Functions.TrigramsWordSimilarity(x.AudioArtist, query) >= .45d)
                        || x.AudioAlbum != null && (EF.Functions.ILike(x.AudioAlbum, pattern, "\\") || query.Length >= 4 && EF.Functions.TrigramsWordSimilarity(x.AudioAlbum, query) >= .45d))
                    .Select(x => x.FileId))
                .Union(_context.UploadedFiles.AsNoTracking()
                    .Where(x => x.Filename != null && (EF.Functions.ILike(x.Filename, pattern, "\\")
                        || query.Length >= 4 && EF.Functions.TrigramsWordSimilarity(x.Filename, query) >= .45d))
                    .Select(x => x.Id));
            fileQuery = fileQuery.Where(x => matchingFileIds.Contains(x.Id));
        }
        var files = await fileQuery.ToListAsync(cancellationToken);
        var fileIds = files.Select(x => x.Id).ToList();

        var entries = fileIds.Count == 0 ? new List<CloudFileEntry>() : await _context.CloudFileEntries.AsNoTracking()
            .Where(x => x.OwnerId == ownerId && fileIds.Contains(x.FileId))
            .ToListAsync(cancellationToken);
        var metadata = fileIds.Count == 0 ? new Dictionary<Guid, FileMetadata>() : await _context.FileMetadata.AsNoTracking()
            .Where(x => fileIds.Contains(x.FileId))
            .ToDictionaryAsync(x => x.FileId, cancellationToken);
        var aliases = fileIds.Count == 0 ? new Dictionary<Guid, FileSearchAlias>() : await _context.FileSearchAliases.AsNoTracking()
            .Where(x => x.OwnerId == ownerId && fileIds.Contains(x.FileId))
            .ToDictionaryAsync(x => x.FileId, cancellationToken);
        var tags = fileIds.Count == 0 ? new Dictionary<Guid, List<FileTag>>() : await _context.FileTags.AsNoTracking()
            .Where(x => x.OwnerId == ownerId && fileIds.Contains(x.FileId))
            .GroupBy(x => x.FileId)
            .ToDictionaryAsync(x => x.Key, x => x.OrderBy(t => t.Value).ToList(), cancellationToken);
        var favoriteIds = fileIds.Count == 0 ? new HashSet<Guid>() : (await _context.FavoriteFiles.AsNoTracking()
            .Where(x => x.OwnerId == ownerId && fileIds.Contains(x.FileId))
            .Select(x => x.FileId)
            .ToListAsync(cancellationToken)).ToHashSet();
        var previews = fileIds.Count == 0 ? new Dictionary<Guid, List<FilePreview>>() : await _context.FilePreviews.AsNoTracking()
            .Where(x => fileIds.Contains(x.OriginalFileId) && x.TargetWidth > 0)
            .GroupBy(x => x.OriginalFileId)
            .ToDictionaryAsync(x => x.Key, x => x.OrderBy(p => p.TargetWidth).ToList(), cancellationToken);

        var albumsQuery = _context.Albums.AsNoTracking().Where(x => x.OwnerId == ownerId);
        var playlistsQuery = _context.MusicPlaylists.AsNoTracking().Where(x => x.OwnerId == ownerId);
        var directoriesQuery = _context.CloudDirectories.AsNoTracking().Where(x => x.OwnerId == ownerId);
        var dynamicFoldersQuery = _context.DynamicFolders.AsNoTracking().Where(x => x.OwnerId == ownerId);
        if (!string.IsNullOrEmpty(query))
        {
            var pattern = LikeContainsPattern(query);
            albumsQuery = albumsQuery.Where(x => (EF.Functions.ILike(x.Name, pattern, "\\")
                || query.Length >= 4 && EF.Functions.TrigramsWordSimilarity(x.Name, query) >= .45d)
                || x.Description != null && (EF.Functions.ILike(x.Description, pattern, "\\") || query.Length >= 4 && EF.Functions.TrigramsWordSimilarity(x.Description, query) >= .45d));
            playlistsQuery = playlistsQuery.Where(x => (EF.Functions.ILike(x.Name, pattern, "\\")
                || query.Length >= 4 && EF.Functions.TrigramsWordSimilarity(x.Name, query) >= .45d)
                || x.Description != null && (EF.Functions.ILike(x.Description, pattern, "\\") || query.Length >= 4 && EF.Functions.TrigramsWordSimilarity(x.Description, query) >= .45d));
            directoriesQuery = directoriesQuery.Where(x => EF.Functions.ILike(x.Name, pattern, "\\")
                || query.Length >= 4 && EF.Functions.TrigramsWordSimilarity(x.Name, query) >= .45d);
            dynamicFoldersQuery = dynamicFoldersQuery.Where(x => EF.Functions.ILike(x.Name, pattern, "\\")
                || query.Length >= 4 && EF.Functions.TrigramsWordSimilarity(x.Name, query) >= .45d);
        }
        var albums = await albumsQuery.ToListAsync(cancellationToken);
        var playlists = await playlistsQuery.ToListAsync(cancellationToken);
        var directories = await directoriesQuery.ToListAsync(cancellationToken);
        var dynamicFolders = await dynamicFoldersQuery.ToListAsync(cancellationToken);

        var sharedFiles = await (from grant in _context.FileGrants.AsNoTracking()
                                 join file in _context.UploadedFiles.AsNoTracking() on grant.FileId equals file.Id
                                 where grant.RecipientId == ownerId
                                 select new SharedFileRow(grant, file)).ToListAsync(cancellationToken);
        var sharedFolders = await (from grant in _context.DirectoryGrants.AsNoTracking()
                                   join dir in _context.CloudDirectories.AsNoTracking() on grant.DirectoryId equals dir.Id
                                   where grant.RecipientId == ownerId
                                   select new SharedFolderRow(grant, dir)).ToListAsync(cancellationToken);
        var sharedPlaylists = await (from grant in _context.MusicPlaylistGrants.AsNoTracking()
                                     join playlist in _context.MusicPlaylists.AsNoTracking() on grant.PlaylistId equals playlist.Id
                                     where grant.RecipientId == ownerId
                                     select new SharedPlaylistRow(grant, playlist)).ToListAsync(cancellationToken);

        return new SearchData(files, entries, metadata, aliases, tags, favoriteIds, previews, albums, playlists,
            directories, dynamicFolders.Concat(SystemDynamicFolders.All()).ToList(), sharedFiles, sharedFolders, sharedPlaylists,
            FileUrlHelper.GetPublicBaseUrl(_configuration, _runSettings));
    }

    private List<RankedHit> BuildHits(SearchSection section, SearchData data, string query)
    {
        var result = new List<RankedHit>();
        switch (section)
        {
            case SearchSection.Photos:
                AddOwnedFiles(result, data, query, DomainMediaKind.Photo, false, SearchHitKind.Photo);
                break;
            case SearchSection.Videos:
                AddOwnedFiles(result, data, query, DomainMediaKind.Video, false, SearchHitKind.Video);
                break;
            case SearchSection.Files:
                AddOwnedFiles(result, data, query, null, false, SearchHitKind.File);
                break;
            case SearchSection.Tracks:
                AddOwnedFiles(result, data, query, DomainMediaKind.Audio, false, SearchHitKind.Track);
                break;
            case SearchSection.Trash:
                AddOwnedFiles(result, data, query, null, true, SearchHitKind.Trash);
                break;
            case SearchSection.Albums:
                foreach (var album in data.Albums)
                    Add(result, SearchHitKind.Album, album.Id.ToString(), album.Name, album.Description, null, false, "", album.UpdatedAt,
                        query, ("name", album.Name), ("description", album.Description));
                break;
            case SearchSection.Playlists:
                foreach (var playlist in data.Playlists)
                    Add(result, SearchHitKind.Playlist, playlist.Id.ToString(), playlist.Name, playlist.Description, null, false, "", playlist.UpdatedAt,
                        query, ("name", playlist.Name), ("description", playlist.Description));
                break;
            case SearchSection.Folders:
                foreach (var folder in data.Directories)
                    Add(result, SearchHitKind.Folder, folder.Id.ToString(), folder.Name, "Папка", null, false, "", folder.UpdatedAt,
                        query, ("name", folder.Name));
                foreach (var folder in data.DynamicFolders)
                    Add(result, SearchHitKind.DynamicFolder, folder.IsSystem ? folder.SystemKey ?? string.Empty : folder.Id.ToString(), folder.Name,
                        folder.IsSystem ? "Системная умная папка" : "Умная папка", null, false, "", folder.UpdatedAt,
                        query, ("name", folder.Name));
                break;
            case SearchSection.Shared:
                foreach (var item in data.SharedFiles)
                    Add(result, SearchHitKind.SharedFile, item.Grant.Id.ToString(), item.File.Filename ?? "Файл", "Доступный файл", item.File.Id, false, "", item.Grant.CreatedAt,
                        query, ("name", item.File.Filename));
                foreach (var item in data.SharedFolders)
                    Add(result, SearchHitKind.SharedFolder, item.Grant.Id.ToString(), item.Directory.Name, "Доступная папка", item.Directory.Id, false, "", item.Grant.CreatedAt,
                        query, ("name", item.Directory.Name));
                foreach (var item in data.SharedPlaylists)
                    Add(result, SearchHitKind.SharedPlaylist, item.Grant.Id.ToString(), item.Playlist.Name, "Доступный плейлист", item.Playlist.Id, false, "", item.Grant.CreatedAt,
                        query, ("name", item.Playlist.Name));
                break;
        }
        return result;
    }

    private void AddOwnedFiles(List<RankedHit> result, SearchData data, string query, DomainMediaKind? onlyKind, bool trash, SearchHitKind kind)
    {
        var liveByFile = data.Entries.Where(x => !x.IsDeleted).GroupBy(x => x.FileId).ToDictionary(x => x.Key, x => x.OrderByDescending(e => e.CreatedAt).First());
        var trashByFile = data.Entries.Where(x => x.IsDeleted).GroupBy(x => x.FileId).ToDictionary(x => x.Key, x => x.OrderByDescending(e => e.DeletedAt).First());

        foreach (var file in data.Files)
        {
            var hasLive = liveByFile.TryGetValue(file.Id, out var liveEntry);
            var hasTrash = trashByFile.TryGetValue(file.Id, out var trashEntry);
            if (trash != (!hasLive && hasTrash))
                continue;
            if (!trash && onlyKind is null && file.MediaKind is DomainMediaKind.Photo or DomainMediaKind.Video or DomainMediaKind.Audio)
                continue;
            if (onlyKind.HasValue && file.MediaKind != onlyKind.Value)
                continue;

            var entry = trash ? trashEntry! : liveEntry;
            var title = entry?.Name ?? file.Filename ?? "Файл";
            data.Metadata.TryGetValue(file.Id, out var meta);
            data.Aliases.TryGetValue(file.Id, out var alias);
            data.Tags.TryGetValue(file.Id, out var tags);
            var subtitle = file.MediaKind == DomainMediaKind.Audio
                ? string.Join(" · ", new[] { meta?.AudioArtist, meta?.AudioAlbum }.Where(x => !string.IsNullOrWhiteSpace(x)))
                : trash ? "В корзине" : FileSubtitle(file.MediaKind, meta);
            var id = trash ? entry!.Id.ToString() : (entry?.Id.ToString() ?? file.Id.ToString());
            var hitKind = trash ? SearchHitKind.Trash : kind;
            var fields = new List<(string Field, string? Value)>
            {
                ("name", title),
                ("alias", alias?.Value),
            };
            fields.AddRange((tags ?? []).Select(t => ("tag", (string?)t.Value)));
            if (file.MediaKind == DomainMediaKind.Audio)
            {
                fields.Add(("title", meta?.AudioTitle));
                fields.Add(("artist", meta?.AudioArtist));
                fields.Add(("album", meta?.AudioAlbum));
            }
            if (file.MediaKind == DomainMediaKind.Document)
            {
                fields.Add(("documentTitle", meta?.DocumentTitle));
                fields.Add(("documentAuthor", meta?.DocumentAuthor));
                fields.Add(("documentSubject", meta?.DocumentSubject));
            }

            Add(result, hitKind, id, title, subtitle, file.Id, data.FavoriteIds.Contains(file.Id), entry?.Id.ToString() ?? string.Empty,
                trash ? entry!.DeletedAt ?? entry.CreatedAt : file.CreatedAt, query, fields.ToArray(), file.MediaKind, file.Size, PreviewUrl(data, file.Id));
        }
    }

    private static string FileSubtitle(DomainMediaKind kind, FileMetadata? metadata) => kind switch
    {
        DomainMediaKind.Document when !string.IsNullOrWhiteSpace(metadata?.DocumentTitle) => metadata.DocumentTitle!,
        DomainMediaKind.Document => "Файл",
        DomainMediaKind.Other => "Файл",
        _ => string.Empty
    };

    private static void Add(
        List<RankedHit> target, SearchHitKind kind, string id, string title, string? subtitle, Guid? fileId,
        bool favorite, string entryId, DateTime sortAt, string query, params (string Field, string? Value)[] fields)
        => Add(target, kind, id, title, subtitle, fileId, favorite, entryId, sortAt, query, fields, DomainMediaKind.Other, 0, string.Empty);

    private static void Add(
        List<RankedHit> target, SearchHitKind kind, string id, string title, string? subtitle, Guid? fileId,
        bool favorite, string entryId, DateTime sortAt, string query, (string Field, string? Value)[] fields,
        DomainMediaKind mediaKind, long size, string previewUrl)
    {
        var match = Match(query, fields);
        if (query.Length > 0 && match is null)
            return;
        var value = match?.Value ?? string.Empty;
        target.Add(new RankedHit(
            new SearchHit
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
                MatchField = match?.Field ?? string.Empty,
                MatchValue = value,
                CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(sortAt, DateTimeKind.Utc)),
                Size = size
            },
            match?.Rank ?? 0,
            match?.Similarity ?? 0,
            sortAt));
    }

    private static SearchSectionResult Page(SearchSection section, List<RankedHit> hits, int requestedLimit, string cursor)
    {
        var limit = requestedLimit <= 0 ? DefaultLimit : Math.Min(requestedLimit, MaxLimit);
        var ordered = hits.OrderByDescending(x => x.Rank)
            .ThenByDescending(x => x.Similarity)
            .ThenByDescending(x => x.SortAt)
            .ThenByDescending(x => x.Hit.Id, StringComparer.Ordinal)
            .ToList();
        var start = ResolveCursor(ordered, cursor);
        var page = ordered.Skip(start).Take(limit + 1).ToList();
        var hasMore = page.Count > limit;
        if (hasMore)
            page.RemoveAt(page.Count - 1);

        var result = new SearchSectionResult { Section = section, HasMore = hasMore };
        result.Hits.AddRange(page.Select(x => x.Hit));
        if (hasMore && page.Count > 0)
            result.NextCursor = EncodeCursor(page[^1]);
        return result;
    }

    private static int ResolveCursor(IReadOnlyList<RankedHit> hits, string cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return 0;
        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor.Replace('-', '+').Replace('_', '/').PadRight((cursor.Length + 3) / 4 * 4, '=')));
            var parts = raw.Split('|');
            if (parts.Length != 5
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rank)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var similarity)
                || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
                throw new FormatException();
            var index = hits.ToList().FindIndex(x => x.Rank == rank
                && Math.Abs(x.Similarity - similarity) < double.Epsilon
                && x.SortAt.Ticks == ticks
                && x.Hit.Kind.ToString() == parts[3]
                && x.Hit.Id == parts[4]);
            if (index < 0)
                throw new FormatException();
            return index + 1;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Некорректный курсор поиска"));
        }
    }

    private static string EncodeCursor(RankedHit hit)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('|',
                hit.Rank.ToString(CultureInfo.InvariantCulture),
                hit.Similarity.ToString("R", CultureInfo.InvariantCulture),
                hit.SortAt.Ticks.ToString(CultureInfo.InvariantCulture),
                hit.Hit.Kind,
                hit.Hit.Id)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static SearchMatch? Match(string query, IEnumerable<(string Field, string? Value)> fields)
    {
        if (query.Length == 0)
            return new SearchMatch(string.Empty, string.Empty, 0, 0);

        SearchMatch? best = null;
        foreach (var (field, raw) in fields)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var value = SearchText.Normalize(raw);
            var (rank, similarity) = value == query ? (4, 1d)
                : value.StartsWith(query, StringComparison.Ordinal) ? (3, 1d)
                : value.Contains(query, StringComparison.Ordinal) ? (2, 1d)
                : query.Length >= 4 ? (1, WordSimilarity(query, value)) : (0, 0d);
            if (rank == 1 && similarity < .45d)
                continue;
            if (rank == 0)
                continue;
            var candidate = new SearchMatch(field, raw!, rank, similarity);
            if (best is null || candidate.Rank > best.Rank || candidate.Rank == best.Rank && candidate.Similarity > best.Similarity)
                best = candidate;
        }
        return best;
    }

    private static double WordSimilarity(string query, string value)
        => value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Append(value)
            .Select(part => TrigramSimilarity(query, part))
            .DefaultIfEmpty(0)
            .Max();

    private static double TrigramSimilarity(string left, string right)
    {
        var a = Trigrams(left);
        var b = Trigrams(right);
        if (a.Count == 0 || b.Count == 0)
            return 0;
        return 2d * a.Intersect(b, StringComparer.Ordinal).Count() / (a.Count + b.Count);
    }

    private static HashSet<string> Trigrams(string value)
    {
        var padded = $"  {value} ";
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i <= padded.Length - 3; i++)
            result.Add(padded.Substring(i, 3));
        return result;
    }

    private static string PreviewUrl(SearchData data, Guid fileId)
    {
        if (!data.Previews.TryGetValue(fileId, out var previews) || previews.Count == 0)
            return string.Empty;
        var preview = previews.FirstOrDefault(x => x.TargetWidth == 512) ?? previews[^1];
        return FileUrlHelper.GenerateDownloadUrl(data.PublicBaseUrl, preview.PreviewFileId);
    }

    private static string LikeContainsPattern(string query)
        => "%" + query.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal) + "%";

    private async Task RequireOwnedFile(Guid fileId, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var exists = await _context.UploadedFiles.AsNoTracking()
            .AnyAsync(x => x.Id == fileId && x.Uploaders.Contains(ownerId), cancellationToken);
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

    private sealed record SearchData(
        List<UploadFile> Files,
        List<CloudFileEntry> Entries,
        Dictionary<Guid, FileMetadata> Metadata,
        Dictionary<Guid, FileSearchAlias> Aliases,
        Dictionary<Guid, List<FileTag>> Tags,
        HashSet<Guid> FavoriteIds,
        Dictionary<Guid, List<FilePreview>> Previews,
        List<Album> Albums,
        List<MusicPlaylist> Playlists,
        List<CloudDirectory> Directories,
        List<DynamicFolder> DynamicFolders,
        List<SharedFileRow> SharedFiles,
        List<SharedFolderRow> SharedFolders,
        List<SharedPlaylistRow> SharedPlaylists,
        string PublicBaseUrl);

    private sealed record SharedFileRow(FileGrant Grant, UploadFile File);
    private sealed record SharedFolderRow(DirectoryGrant Grant, CloudDirectory Directory);
    private sealed record SharedPlaylistRow(MusicPlaylistGrant Grant, MusicPlaylist Playlist);
    private sealed record SearchMatch(string Field, string Value, int Rank, double Similarity);
    private sealed record RankedHit(SearchHit Hit, int Rank, double Similarity, DateTime SortAt);
}
