using System.Text.Json;

using BarkCloud.Proto.Files;
using BarkCloud.Proto.Torrent;
using BarkCloud.Web.Auth;
using BarkCloud.Web.Infrastructure;

using Grpc.Core;

namespace BarkCloud.Web.Endpoints;

/// <summary>Объединяет поиск Files и Torrent в один same-origin JSON API для SPA.</summary>
public static class SearchEndpoints
{
    private static readonly JsonSerializerOptions Json = new();
    private static readonly IReadOnlyDictionary<string, SearchSection> Sections = new Dictionary<string, SearchSection>(StringComparer.OrdinalIgnoreCase)
    {
        ["photos"] = SearchSection.Photos,
        ["videos"] = SearchSection.Videos,
        ["files"] = SearchSection.Files,
        ["tracks"] = SearchSection.Tracks,
        ["albums"] = SearchSection.Albums,
        ["playlists"] = SearchSection.Playlists,
        ["folders"] = SearchSection.Folders,
        ["shared"] = SearchSection.Shared,
        ["trash"] = SearchSection.Trash,
    };

    public static void MapSearchEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/search");

        api.MapGet("/suggest", async (HttpContext http, AuthGateway auth, SearchApi.SearchApiClient files, TorrentApi.TorrentApiClient torrents, string? q) =>
            await Search(http, auth, files, torrents, q, null, null, suggestion: true));

        api.MapGet("", async (HttpContext http, AuthGateway auth, SearchApi.SearchApiClient files, TorrentApi.TorrentApiClient torrents,
            string? q, string? section, string? cursor) =>
            await Search(http, auth, files, torrents, q, section, cursor, suggestion: false));

        api.MapGet("/hit", async (HttpContext http, AuthGateway auth, SearchApi.SearchApiClient files, TorrentApi.TorrentApiClient torrents,
            string? kind, string? id) =>
        {
            var user = await auth.AuthenticateAsync(http);
            if (user is null)
                return Results.Json(new { error = "Не авторизован" }, Json, statusCode: 401);
            if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(id))
                return Results.Json(new { error = "Не указан результат" }, Json, statusCode: 400);

            try
            {
                var token = BrowserContext.UserToken(user.AccessToken);
                if (kind.Equals("torrent", StringComparison.OrdinalIgnoreCase))
                {
                    var torrent = await torrents.GetTorrentAsync(new TorrentIdRequest { Id = id }, token,
                        deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: http.RequestAborted);
                    return Results.Json(ToTorrentJson(torrent), Json);
                }

                if (!TryParseKind(kind, out var hitKind))
                    return Results.Json(new { error = "Неизвестный тип результата" }, Json, statusCode: 400);
                var hit = await files.ResolveHitAsync(new SearchHitReference { Kind = hitKind, Id = id }, token,
                    deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: http.RequestAborted);
                return Results.Json(ToJson(hit), Json);
            }
            catch (RpcException ex) when (ex.StatusCode is StatusCode.NotFound or StatusCode.PermissionDenied)
            {
                return Results.Json(new { error = "Результат больше недоступен" }, Json, statusCode: 404);
            }
            catch (RpcException ex)
            {
                return Results.Json(new { error = ex.Status.Detail }, Json, statusCode: 502);
            }
        });

        var filesApi = app.MapGroup("/api/files");
        filesApi.MapPut("/{fileId}/search-metadata", async (HttpContext http, AuthGateway auth, SearchApi.SearchApiClient files,
            string fileId, SearchMetadataBody body) =>
        {
            var user = await auth.AuthenticateAsync(http);
            if (user is null)
                return Results.Json(new { error = "Не авторизован" }, Json, statusCode: 401);
            if (!Guid.TryParse(fileId, out _))
                return Results.Json(new { error = "Некорректный id файла" }, Json, statusCode: 400);

            try
            {
                var req = new ReplaceFileSearchMetadataRequest { FileId = fileId, Alias = body.Alias ?? string.Empty };
                req.Tags.AddRange(body.Tags ?? Array.Empty<string>());
                var result = await files.ReplaceFileSearchMetadataAsync(req, BrowserContext.UserToken(user.AccessToken),
                    deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: http.RequestAborted);
                return Results.Json(new { alias = result.Alias, tags = result.Tags.ToArray() }, Json);
            }
            catch (RpcException ex) when (ex.StatusCode is StatusCode.InvalidArgument or StatusCode.FailedPrecondition)
            {
                return Results.Json(new { error = ex.Status.Detail }, Json, statusCode: 400);
            }
            catch (RpcException ex) when (ex.StatusCode is StatusCode.NotFound or StatusCode.PermissionDenied)
            {
                return Results.Json(new { error = "Файл не найден" }, Json, statusCode: 404);
            }
            catch (RpcException ex)
            {
                return Results.Json(new { error = ex.Status.Detail }, Json, statusCode: 502);
            }
        });
    }

    private static async Task<IResult> Search(
        HttpContext http,
        AuthGateway auth,
        SearchApi.SearchApiClient files,
        TorrentApi.TorrentApiClient torrents,
        string? rawQuery,
        string? rawSection,
        string? cursor,
        bool suggestion)
    {
        var user = await auth.AuthenticateAsync(http);
        if (user is null)
            return Results.Json(new { error = "Не авторизован" }, Json, statusCode: 401);

        var query = (rawQuery ?? string.Empty).Trim();
        if (query.Length > 200)
            return Results.Json(new { error = "Поисковый запрос не длиннее 200 символов" }, Json, statusCode: 400);
        if (query.Length < 2)
            return Results.Json(new { query, sections = Array.Empty<object>() }, Json);

        var token = BrowserContext.UserToken(user.AccessToken);
        var onlyTorrents = string.Equals(rawSection, "torrents", StringComparison.OrdinalIgnoreCase);
        SearchSection? section = null;
        if (!string.IsNullOrWhiteSpace(rawSection) && !onlyTorrents)
        {
            if (!Sections.TryGetValue(rawSection, out var parsed))
                return Results.Json(new { error = "Неизвестная группа поиска" }, Json, statusCode: 400);
            section = parsed;
        }

        var filesRequest = new SearchRequest { Query = query };
        if (!onlyTorrents)
        {
            if (section.HasValue)
            {
                filesRequest.Pages.Add(new SearchSectionPage
                {
                    Section = section.Value,
                    Limit = suggestion ? 3 : IsGrid(section.Value) ? 12 : 20,
                    Cursor = cursor ?? string.Empty
                });
            }
            else
            {
                foreach (var pair in Sections)
                    filesRequest.Pages.Add(new SearchSectionPage
                    {
                        Section = pair.Value,
                        Limit = suggestion ? 3 : IsGrid(pair.Value) ? 12 : 20
                    });
            }
        }

        var deadline = DateTime.UtcNow.AddSeconds(suggestion ? 2 : 5);
        var filesTask = onlyTorrents
            ? Task.FromResult(new SearchResponse())
            : files.SearchAsync(filesRequest, token, deadline: deadline, cancellationToken: http.RequestAborted).ResponseAsync;
        var includeTorrents = section is null || onlyTorrents;
        var torrentTask = includeTorrents
            ? torrents.SearchTorrentsAsync(new SearchTorrentsRequest
            {
                Query = query,
                Limit = suggestion ? 3 : 20,
                Cursor = onlyTorrents ? cursor ?? string.Empty : string.Empty
            }, token, deadline: deadline, cancellationToken: http.RequestAborted).ResponseAsync
            : Task.FromResult<SearchTorrentsResponse?>(null);

        SearchResponse filesResponse;
        try
        {
            filesResponse = await filesTask;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument)
        {
            return Results.Json(new { error = ex.Status.Detail }, Json, statusCode: 400);
        }
        catch (RpcException ex)
        {
            return Results.Json(new { error = ex.Status.Detail }, Json, statusCode: 502);
        }

        SearchTorrentsResponse? torrentsResponse = null;
        var torrentUnavailable = false;
        if (includeTorrents)
        {
            try
            {
                torrentsResponse = await torrentTask;
            }
            catch (RpcException)
            {
                torrentUnavailable = true;
            }
        }

        var sections = filesResponse.Sections.Select(s => new
        {
            key = ToKey(s.Section),
            items = s.Hits.Select(ToJson).ToArray(),
            nextCursor = s.NextCursor,
            hasMore = s.HasMore,
            unavailable = false
        }).Cast<object>().ToList();
        if (includeTorrents)
        {
            sections.Add(new
            {
                key = "torrents",
                items = torrentsResponse?.Torrents.Select(ToTorrentJson).ToArray() ?? Array.Empty<object>(),
                nextCursor = torrentsResponse?.NextCursor ?? string.Empty,
                hasMore = torrentsResponse?.HasMore ?? false,
                unavailable = torrentUnavailable
            });
        }
        return Results.Json(new { query, sections }, Json);
    }

    private static bool IsGrid(SearchSection section) => section is SearchSection.Photos or SearchSection.Videos or SearchSection.Albums or SearchSection.Playlists;

    private static string ToKey(SearchSection section) => section switch
    {
        SearchSection.Photos => "photos",
        SearchSection.Videos => "videos",
        SearchSection.Files => "files",
        SearchSection.Tracks => "tracks",
        SearchSection.Albums => "albums",
        SearchSection.Playlists => "playlists",
        SearchSection.Folders => "folders",
        SearchSection.Shared => "shared",
        SearchSection.Trash => "trash",
        _ => "unknown"
    };

    private static bool TryParseKind(string value, out SearchHitKind kind)
    {
        kind = value.ToLowerInvariant() switch
        {
            "photo" => SearchHitKind.Photo,
            "video" => SearchHitKind.Video,
            "file" => SearchHitKind.File,
            "track" => SearchHitKind.Track,
            "album" => SearchHitKind.Album,
            "playlist" => SearchHitKind.Playlist,
            "folder" => SearchHitKind.Folder,
            "dynamicfolder" => SearchHitKind.DynamicFolder,
            "sharedfile" => SearchHitKind.SharedFile,
            "sharedfolder" => SearchHitKind.SharedFolder,
            "sharedplaylist" => SearchHitKind.SharedPlaylist,
            "trash" => SearchHitKind.Trash,
            _ => SearchHitKind.Unspecified
        };
        return kind != SearchHitKind.Unspecified;
    }

    private static object ToJson(SearchHit hit) => new
    {
        kind = ToKind(hit.Kind),
        id = hit.Id,
        fileId = hit.FileId,
        entryId = hit.EntryId,
        title = hit.Title,
        subtitle = hit.Subtitle,
        previewUrl = hit.PreviewUrl,
        mediaKind = hit.MediaKind.ToString().Replace("MediaKind", "", StringComparison.Ordinal).ToLowerInvariant(),
        favorite = hit.Favorite,
        matchField = hit.MatchField,
        matchValue = hit.MatchValue,
        createdAt = hit.CreatedAt?.ToDateTimeOffset(),
        size = hit.Size,
    };

    private static string ToKind(SearchHitKind kind) => kind switch
    {
        SearchHitKind.Photo => "photo",
        SearchHitKind.Video => "video",
        SearchHitKind.File => "file",
        SearchHitKind.Track => "track",
        SearchHitKind.Album => "album",
        SearchHitKind.Playlist => "playlist",
        SearchHitKind.Folder => "folder",
        SearchHitKind.DynamicFolder => "dynamicFolder",
        SearchHitKind.SharedFile => "sharedFile",
        SearchHitKind.SharedFolder => "sharedFolder",
        SearchHitKind.SharedPlaylist => "sharedPlaylist",
        SearchHitKind.Trash => "trash",
        _ => "unknown"
    };

    private static object ToTorrentJson(TorrentInfo torrent) => new
    {
        kind = "torrent",
        id = torrent.Id,
        fileId = "",
        entryId = "",
        title = torrent.Name,
        subtitle = torrent.InfoHash,
        previewUrl = "",
        mediaKind = "other",
        favorite = false,
        matchField = "name",
        matchValue = torrent.Name,
        createdAt = torrent.AddedAt?.ToDateTimeOffset(),
        size = torrent.TotalSize,
        status = torrent.Status.ToString().Replace("TorrentStatus", "", StringComparison.Ordinal).ToLowerInvariant(),
        progress = torrent.Progress,
    };

    private sealed record SearchMetadataBody(string? Alias, string[]? Tags);
}
