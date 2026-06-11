using System.Net.Http.Headers;
using System.Text.Json;

using BarkCloud.Proto.Files;
using BarkCloud.Proto.Users;
using BarkCloud.Web.Auth;
using BarkCloud.Web.Infrastructure;
using BarkCloud.Web.Rendering;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

namespace BarkCloud.Web.Endpoints;

/// <summary>
/// Same-origin JSON-API для React-страниц (Фото / Видео / Файлы). Проксирует вызовы
/// в Files-сервис (CloudApi / AlbumApi / FilesApi) с пользовательским токеном из cookie.
/// Загрузка файла идёт через веб-сервер (без CORS на Files).
/// </summary>
public static class CloudApiEndpoints
{
    // дефолтный энкодер экранирует < > & — безопасно; имена свойств уже в нужном регистре
    private static readonly JsonSerializerOptions Json = new();

    public static void MapCloudApiEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // ───────────────────────── Каркас (профиль/хранилище/версия) ─────────────────────────

        // Данные общего каркаса SPA (Sidebar/Topbar/Footbar). Раньше инлайнились в shared.jsx
        // через плейсхолдеры {{ }}; теперь SPA грузит их через /api/me при монтировании AppShell.
        api.MapGet("/me", async (HttpContext http, AuthGateway auth, PageDataBuilder data) =>
        {
            var user = await auth.AuthenticateAsync(http);
            if (user is null)
                return Results.Json(new { error = "Не авторизован" }, Json, statusCode: 401);

            var v = await data.BuildShellAsync(user, http);
            int.TryParse(v.GetValueOrDefault("storage.percent"), out var percent);

            return Results.Json(new
            {
                user = new
                {
                    initials = v.GetValueOrDefault("user.initials"),
                    displayName = v.GetValueOrDefault("user.display_name"),
                    role = v.GetValueOrDefault("user.role"),
                    avatarUrl = v.GetValueOrDefault("user.avatar_url")
                },
                storage = new
                {
                    usedLabel = v.GetValueOrDefault("storage.used_label"),
                    totalLabel = v.GetValueOrDefault("storage.total_label"),
                    percent
                },
                app = new
                {
                    version = v.GetValueOrDefault("app.version"),
                    edition = v.GetValueOrDefault("app.edition")
                },
                server = new { host = v.GetValueOrDefault("server.host") },
                sync = new
                {
                    status = v.GetValueOrDefault("sync.status"),
                    lastAt = v.GetValueOrDefault("sync.last_at")
                }
            }, Json);
        });

        // Лёгкий рефетч блока хранилища (Sidebar) при переключении вкладок SPA.
        api.MapGet("/storage", async (HttpContext http, AuthGateway auth, PageDataBuilder data) =>
        {
            var user = await auth.AuthenticateAsync(http);
            if (user is null)
                return Results.Json(new { error = "Не авторизован" }, Json, statusCode: 401);

            return Results.Json(await data.BuildStorageAsync(user), Json);
        });

        // ───────────────────────── Каталоги ─────────────────────────

        api.MapGet("/cloud/list", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, string? dir) =>
            await Guarded(http, auth, async token =>
            {
                var req = new ListDirectoryRequest();
                if (!string.IsNullOrEmpty(dir)) req.DirectoryId = dir;

                var listing = await cloud.ListDirectoryDetailedAsync(req, token);
                return Results.Json(new
                {
                    dirs = listing.Subdirs.Select(CloudJson.Dir).ToArray(),
                    files = listing.Files.Select(CloudJson.Entry).ToArray()
                }, Json);
            }));

        // Поиск файлов пользователя по имени (по всему облаку), cursor-пагинация.
        // kind: media (фото+видео) | photo | video; не задан — все типы.
        api.MapGet("/cloud/search", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud,
            string? q, string? kind, int? limit, string? cursorAt, string? cursorId) =>
            await Guarded(http, auth, async token =>
            {
                var query = (q ?? "").Trim();
                if (query.Length == 0)
                    return Results.Json(new { files = Array.Empty<object>(), nextCursorAt = (DateTimeOffset?)null, nextCursorId = "" }, Json);

                var req = new SearchFilesRequest { Query = query, Limit = limit is > 0 and <= 200 ? limit.Value : 50 };
                switch (kind)
                {
                    case "media": req.KindFilter.Add(MediaKind.Photo); req.KindFilter.Add(MediaKind.Video); break;
                    case "photo": req.KindFilter.Add(MediaKind.Photo); break;
                    case "video": req.KindFilter.Add(MediaKind.Video); break;
                }
                if (DateTimeOffset.TryParse(cursorAt, out var dt))
                    req.CursorCreatedAt = Timestamp.FromDateTimeOffset(dt.ToUniversalTime());
                if (!string.IsNullOrEmpty(cursorId))
                    req.CursorEntryId = cursorId;

                var resp = await cloud.SearchFilesAsync(req, token);
                return Results.Json(new
                {
                    files = resp.Files.Select(CloudJson.Entry).ToArray(),
                    nextCursorAt = resp.NextCursorCreatedAt?.ToDateTimeOffset(),
                    nextCursorId = resp.NextCursorEntryId
                }, Json);
            }));

        api.MapPost("/cloud/dir", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, DirCreate body) =>
            await Guarded(http, auth, async token =>
            {
                var info = await cloud.CreateDirectoryAsync(
                    new CreateDirectoryRequest { ParentId = body.ParentId ?? "", Name = body.Name }, token);
                return Results.Json(CloudJson.Dir(info), Json);
            }));

        api.MapPost("/cloud/dir/rename", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, RenameReq body) =>
            await Guarded(http, auth, async token =>
            {
                await cloud.RenameDirectoryAsync(new RenameDirectoryRequest { DirectoryId = body.Id, NewName = body.Name }, token);
                return Results.Json(new { ok = true }, Json);
            }));

        api.MapPost("/cloud/dir/move", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, MoveReq body) =>
            await Guarded(http, auth, async token =>
            {
                await cloud.MoveDirectoryAsync(new MoveDirectoryRequest { DirectoryId = body.Id, NewParentId = body.ParentId ?? "" }, token);
                return Results.Json(new { ok = true }, Json);
            }));

        api.MapPost("/cloud/dir/delete", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, IdReq body) =>
            await Guarded(http, auth, async token =>
            {
                await cloud.DeleteDirectoryAsync(new DeleteDirectoryRequest { DirectoryId = body.Id }, token);
                return Results.Json(new { ok = true }, Json);
            }));

        // Путь до объекта в иерархии (для «Показать в папке» из галереи): segments — папки от корня до файла.
        api.MapGet("/cloud/path", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, string? entry, string? dir) =>
            await Guarded(http, auth, async token =>
            {
                var req = new GetPathRequest();
                if (!string.IsNullOrEmpty(entry)) req.EntryId = entry;
                else if (!string.IsNullOrEmpty(dir)) req.DirectoryId = dir;
                else return Results.Json(new { error = "Не указан entry или dir" }, Json, statusCode: 400);

                var resp = await cloud.GetPathAsync(req, token);
                return Results.Json(new
                {
                    segments = resp.Segments.Select(s => new { id = s.Id, name = s.Name }).ToArray(),
                    fullPath = resp.FullPath
                }, Json);
            }));

        // ───────────────────────── Записи файлов в каталоге ─────────────────────────

        api.MapPost("/cloud/attach", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, AttachReq body) =>
            await Guarded(http, auth, async token =>
            {
                await cloud.AttachFileAsync(
                    new AttachFileRequest { DirectoryId = body.Dir ?? "", FileId = body.FileId, Name = body.Name, RouteByMediaKind = body.RouteByMediaKind }, token);
                return Results.Json(new { ok = true }, Json);
            }));

        api.MapPost("/cloud/entry/rename", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, EntryRenameReq body) =>
            await Guarded(http, auth, async token =>
            {
                await cloud.RenameFileEntryAsync(new RenameFileEntryRequest { EntryId = body.EntryId, NewName = body.Name }, token);
                return Results.Json(new { ok = true }, Json);
            }));

        api.MapPost("/cloud/entry/move", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, EntryMoveReq body) =>
            await Guarded(http, auth, async token =>
            {
                await cloud.MoveFileEntryAsync(new MoveFileEntryRequest { EntryId = body.EntryId, NewDirectoryId = body.Dir ?? "" }, token);
                return Results.Json(new { ok = true }, Json);
            }));

        api.MapPost("/cloud/entry/delete", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, EntryIdReq body) =>
            await Guarded(http, auth, async token =>
            {
                await cloud.DeleteFileEntryAsync(new DeleteFileEntryRequest { EntryId = body.EntryId }, token);
                return Results.Json(new { ok = true }, Json);
            }));

        api.MapPost("/cloud/entries/delete", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, EntryIdsReq body) =>
            await Guarded(http, auth, async token =>
            {
                var rawIds = body.EntryIds ?? Array.Empty<string>();
                var invalid = new List<string>();
                var ids = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var raw in rawIds)
                {
                    if (!Guid.TryParse(raw, out var id))
                    {
                        invalid.Add(raw);
                        continue;
                    }

                    var normalized = id.ToString();
                    if (seen.Add(normalized))
                        ids.Add(normalized);
                }

                var deleted = 0;
                foreach (var chunk in ids.Chunk(100))
                {
                    var req = new DeleteFileEntriesRequest();
                    req.EntryIds.AddRange(chunk);
                    var resp = await cloud.DeleteFileEntriesAsync(req, token);
                    deleted += resp.DeletedCount;
                }

                var total = ids.Count + invalid.Count;
                return Results.Json(new
                {
                    total,
                    succeeded = deleted,
                    failed = total - deleted,
                    invalidIds = invalid.ToArray()
                }, Json);
            }));

        // ───────────────────────── Корзина ─────────────────────────

        api.MapGet("/cloud/trash", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud,
            int? limit, string? cursorAt, string? cursorId) =>
            await Guarded(http, auth, async token =>
            {
                var req = new ListTrashRequest { Limit = limit is > 0 and <= 200 ? limit.Value : 60 };
                if (DateTimeOffset.TryParse(cursorAt, out var dt))
                    req.CursorDeletedAt = Timestamp.FromDateTimeOffset(dt.ToUniversalTime());
                if (!string.IsNullOrEmpty(cursorId))
                    req.CursorEntryId = cursorId;

                var resp = await cloud.ListTrashAsync(req, token);
                return Results.Json(new
                {
                    items = resp.Items.Select(CloudJson.Trash).ToArray(),
                    nextCursorAt = resp.NextCursorDeletedAt?.ToDateTimeOffset(),
                    nextCursorId = resp.NextCursorEntryId
                }, Json);
            }));

        api.MapPost("/cloud/trash/restore", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, EntryIdReq body) =>
            await Guarded(http, auth, async token =>
            {
                await cloud.RestoreFromTrashAsync(new RestoreFromTrashRequest { EntryId = body.EntryId }, token);
                return Results.Json(new { ok = true }, Json);
            }));

        api.MapPost("/cloud/trash/restore-batch", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, EntryIdsReq body) =>
            await Guarded(http, auth, async token =>
            {
                var result = await RunIdBatch(body.EntryIds, async entryId =>
                    await cloud.RestoreFromTrashAsync(new RestoreFromTrashRequest { EntryId = entryId }, token));
                return Results.Json(result, Json);
            }));

        api.MapPost("/cloud/trash/purge", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, EntryIdReq body) =>
            await Guarded(http, auth, async token =>
            {
                await cloud.DeleteFromTrashAsync(new DeleteFromTrashRequest { EntryId = body.EntryId }, token);
                return Results.Json(new { ok = true }, Json);
            }));

        api.MapPost("/cloud/trash/purge-batch", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, EntryIdsReq body) =>
            await Guarded(http, auth, async token =>
            {
                var result = await RunIdBatch(body.EntryIds, async entryId =>
                    await cloud.DeleteFromTrashAsync(new DeleteFromTrashRequest { EntryId = entryId }, token));
                return Results.Json(result, Json);
            }));

        api.MapPost("/cloud/trash/empty", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud) =>
            await Guarded(http, auth, async token =>
            {
                await cloud.EmptyTrashAsync(new EmptyTrashRequest(), token);
                return Results.Json(new { ok = true }, Json);
            }));

        // ───────────────────────── Галерея (фото / видео) ─────────────────────────

        api.MapGet("/cloud/media", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud,
            string? kind, int? limit, string? cursorAt, string? cursorId) =>
            await Guarded(http, auth, async token =>
            {
                var req = new ListUserMediaRequest
                {
                    Kind = kind == "video" ? MediaKind.Video : MediaKind.Photo,
                    Limit = limit is > 0 and <= 200 ? limit.Value : 60
                };
                if (DateTimeOffset.TryParse(cursorAt, out var dt))
                    req.CursorCreatedAt = Timestamp.FromDateTimeOffset(dt.ToUniversalTime());
                if (!string.IsNullOrEmpty(cursorId))
                    req.CursorFileId = cursorId;

                var resp = await cloud.ListUserMediaAsync(req, token);
                return Results.Json(new
                {
                    items = resp.Items.Where(i => i.File is not null).Select(CloudJson.MediaItem).ToArray(),
                    nextCursorAt = resp.NextCursorCreatedAt?.ToDateTimeOffset(),
                    nextCursorId = resp.NextCursorFileId
                }, Json);
            }));

        api.MapPost("/cloud/media/delete", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, FileIdReq body) =>
            await Guarded(http, auth, async token =>
            {
                await cloud.DeleteUserMediaAsync(new DeleteUserMediaRequest { FileId = body.FileId }, token);
                return Results.Json(new { ok = true }, Json);
            }));

        api.MapPost("/cloud/media/delete-batch", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, FileIdsReq body) =>
            await Guarded(http, auth, async token =>
            {
                var result = await RunIdBatch(body.FileIds, async fileId =>
                    await cloud.DeleteUserMediaAsync(new DeleteUserMediaRequest { FileId = fileId }, token));
                return Results.Json(result, Json);
            }));

        // «Воспоминания — В этот день»: фото/видео за сегодняшнюю дату прошлых лет, по группам-годам.
        api.MapGet("/cloud/memories", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud,
            int? month, int? day, int? perYear) =>
            await Guarded(http, auth, async token =>
            {
                var req = new GetMemoriesRequest
                {
                    Month = month is >= 1 and <= 12 ? month.Value : 0,
                    Day = day is >= 1 and <= 31 ? day.Value : 0,
                    PerYearLimit = perYear is > 0 ? perYear.Value : 0
                };
                var resp = await cloud.GetMemoriesAsync(req, token);
                return Results.Json(new { groups = resp.Groups.Select(CloudJson.MemoryGroup).ToArray() }, Json);
            }));

        // Заменить превью видео загруженной картинкой-кадром (sourceImageFileId — уже загруженный файл).
        api.MapPost("/cloud/video/thumbnail", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, VideoThumbReq body) =>
            await Guarded(http, auth, async token =>
            {
                await cloud.SetVideoThumbnailAsync(
                    new SetVideoThumbnailRequest { VideoFileId = body.VideoFileId, SourceImageFileId = body.ImageFileId }, token);
                return Results.Json(new { ok = true }, Json);
            }));

        // ───────────────────────── Избранное ─────────────────────────

        api.MapGet("/cloud/favorites", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud,
            int? limit, string? cursorAt, string? cursorId) =>
            await Guarded(http, auth, async token =>
            {
                var req = new ListFavoritesRequest { Limit = limit is > 0 and <= 200 ? limit.Value : 60 };
                if (DateTimeOffset.TryParse(cursorAt, out var dt))
                    req.CursorFavoritedAt = Timestamp.FromDateTimeOffset(dt.ToUniversalTime());
                if (!string.IsNullOrEmpty(cursorId))
                    req.CursorFileId = cursorId;

                var resp = await cloud.ListFavoritesAsync(req, token);
                return Results.Json(new
                {
                    items = resp.Items.Where(i => i.File is not null).Select(i => CloudJson.Media(i.File)).ToArray(),
                    nextCursorAt = resp.NextCursorFavoritedAt?.ToDateTimeOffset(),
                    nextCursorId = resp.NextCursorFileId
                }, Json);
            }));

        api.MapPost("/cloud/favorites/add", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, FileIdReq body) =>
            await Guarded(http, auth, async token =>
            {
                await cloud.AddFavoriteAsync(new AddFavoriteRequest { FileId = body.FileId }, token);
                return Results.Json(new { ok = true }, Json);
            }));

        api.MapPost("/cloud/favorites/remove", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, FileIdReq body) =>
            await Guarded(http, auth, async token =>
            {
                await cloud.RemoveFavoriteAsync(new RemoveFavoriteRequest { FileId = body.FileId }, token);
                return Results.Json(new { ok = true }, Json);
            }));

        // ───────────────────────── Публичные ссылки ─────────────────────────

        api.MapGet("/shares", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud,
            int? limit, string? cursorAt, string? cursorId) =>
            await Guarded(http, auth, async token =>
            {
                var req = new ListMySharesRequest { Limit = limit is > 0 and <= 200 ? limit.Value : 60 };
                if (DateTimeOffset.TryParse(cursorAt, out var dt))
                    req.CursorCreatedAt = Timestamp.FromDateTimeOffset(dt.ToUniversalTime());
                if (!string.IsNullOrEmpty(cursorId))
                    req.CursorShareId = cursorId;

                var resp = await cloud.ListMySharesAsync(req, token);
                return Results.Json(new
                {
                    items = resp.Shares.Select(s => ShareJson(http, s)).ToArray(),
                    nextCursorAt = resp.NextCursorCreatedAt?.ToDateTimeOffset(),
                    nextCursorId = resp.NextCursorShareId
                }, Json);
            }));

        api.MapPost("/shares", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, ShareCreateReq body) =>
            await Guarded(http, auth, async token =>
            {
                var info = await cloud.CreateShareAsync(
                    new CreateShareRequest { FileId = body.FileId, Name = body.Name ?? "" }, token);
                return Results.Json(ShareJson(http, info), Json);
            }));

        api.MapPost("/shares/revoke", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, ShareIdReq body) =>
            await Guarded(http, auth, async token =>
            {
                await cloud.RevokeShareAsync(new RevokeShareRequest { ShareId = body.ShareId }, token);
                return Results.Json(new { ok = true }, Json);
            }));

        // ───────────────────────── Публичные папки (динамическая страница /f/{token}) ─────────────────────────

        api.MapGet("/folder-shares", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud,
            int? limit, string? cursorAt, string? cursorId) =>
            await Guarded(http, auth, async token =>
            {
                var req = new ListMyFolderSharesRequest { Limit = limit is > 0 and <= 200 ? limit.Value : 60 };
                if (DateTimeOffset.TryParse(cursorAt, out var dt))
                    req.CursorCreatedAt = Timestamp.FromDateTimeOffset(dt.ToUniversalTime());
                if (!string.IsNullOrEmpty(cursorId))
                    req.CursorFolderShareId = cursorId;

                var resp = await cloud.ListMyFolderSharesAsync(req, token);
                return Results.Json(new
                {
                    items = resp.Shares.Select(s => FolderShareJson(http, s)).ToArray(),
                    nextCursorAt = resp.NextCursorCreatedAt?.ToDateTimeOffset(),
                    nextCursorId = resp.NextCursorFolderShareId
                }, Json);
            }));

        api.MapPost("/folder-shares", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, FolderShareCreateReq body) =>
            await Guarded(http, auth, async token =>
            {
                var info = await cloud.CreateFolderShareAsync(
                    new CreateFolderShareRequest { DirectoryId = body.DirectoryId, Name = body.Name ?? "" }, token);
                return Results.Json(FolderShareJson(http, info), Json);
            }));

        api.MapPost("/folder-shares/revoke", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, FolderShareIdReq body) =>
            await Guarded(http, auth, async token =>
            {
                await cloud.RevokeFolderShareAsync(new RevokeFolderShareRequest { FolderShareId = body.FolderShareId }, token);
                return Results.Json(new { ok = true }, Json);
            }));

        // ───────────────────────── Публичные альбомы (динамическая страница /al/{token}) ─────────────────────────

        api.MapGet("/album-shares", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud,
            int? limit, string? cursorAt, string? cursorId) =>
            await Guarded(http, auth, async token =>
            {
                var req = new ListMyAlbumSharesRequest { Limit = limit is > 0 and <= 200 ? limit.Value : 60 };
                if (DateTimeOffset.TryParse(cursorAt, out var dt))
                    req.CursorCreatedAt = Timestamp.FromDateTimeOffset(dt.ToUniversalTime());
                if (!string.IsNullOrEmpty(cursorId))
                    req.CursorAlbumShareId = cursorId;

                var resp = await cloud.ListMyAlbumSharesAsync(req, token);
                return Results.Json(new
                {
                    items = resp.Shares.Select(s => AlbumShareJson(http, s)).ToArray(),
                    nextCursorAt = resp.NextCursorCreatedAt?.ToDateTimeOffset(),
                    nextCursorId = resp.NextCursorAlbumShareId
                }, Json);
            }));

        api.MapPost("/album-shares", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, AlbumShareCreateReq body) =>
            await Guarded(http, auth, async token =>
            {
                var info = await cloud.CreateAlbumShareAsync(
                    new CreateAlbumShareRequest { AlbumId = body.AlbumId, Name = body.Name ?? "" }, token);
                return Results.Json(AlbumShareJson(http, info), Json);
            }));

        api.MapPost("/album-shares/revoke", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, AlbumShareIdReq body) =>
            await Guarded(http, auth, async token =>
            {
                await cloud.RevokeAlbumShareAsync(new RevokeAlbumShareRequest { AlbumShareId = body.AlbumShareId }, token);
                return Results.Json(new { ok = true }, Json);
            }));

        // ───────────────────────── Шаринг между пользователями ─────────────────────────

        // Поиск получателей (юзернейм/имя, минимум 2 символа).
        api.MapGet("/shared/users/search", async (HttpContext http, AuthGateway auth, UsersApi.UsersApiClient users, string? q, int? limit) =>
            await Guarded(http, auth, async token =>
            {
                var query = (q ?? "").Trim();
                if (query.Length < 2)
                    return Results.Json(new { users = Array.Empty<object>() }, Json);

                var resp = await users.SearchUsersAsync(
                    new SearchUsersRequest { Query = query, Limit = limit is > 0 and <= 50 ? limit.Value : 20 }, token);
                return Results.Json(new { users = resp.Users.Select(UserJson).ToArray() }, Json);
            }));

        // Поделиться файлом с пользователем.
        api.MapPost("/shared/grant", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, GrantReq body) =>
            await Guarded(http, auth, async token =>
            {
                await cloud.ShareFileWithUserAsync(
                    new ShareFileWithUserRequest { FileId = body.FileId, RecipientUserId = body.RecipientUserId }, token);
                return Results.Json(new { ok = true }, Json);
            }));

        // Отозвать грант доступа.
        api.MapPost("/shared/revoke-grant", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, GrantIdReq body) =>
            await Guarded(http, auth, async token =>
            {
                await cloud.RevokeUserShareAsync(new RevokeUserShareRequest { GrantId = body.GrantId }, token);
                return Results.Json(new { ok = true }, Json);
            }));

        // С кем поделён файл (управление).
        api.MapGet("/shared/outgoing", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud,
            UsersServerApi.UsersServerApiClient usersServer, string fileId) =>
            await Guarded(http, auth, async token =>
            {
                var resp = await cloud.ListMyOutgoingSharesAsync(new ListMyOutgoingSharesRequest { FileId = fileId }, token);
                var byId = await ResolveUsers(usersServer, resp.Items.Select(i => i.RecipientUserId));
                return Results.Json(new
                {
                    items = resp.Items.Select(i => new
                    {
                        grantId = i.GrantId,
                        sharedAt = i.SharedAt?.ToDateTimeOffset(),
                        user = byId.TryGetValue(i.RecipientUserId, out var u) ? UserJson(u) : MinimalUserJson(i.RecipientUserId)
                    }).ToArray()
                }, Json);
            }));

        // Я поделился: все мои исходящие гранты (файлы + кому), от новых к старым.
        api.MapGet("/shared/i-shared", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud,
            UsersServerApi.UsersServerApiClient usersServer, int? limit, string? cursorAt, string? cursorId) =>
            await Guarded(http, auth, async token =>
            {
                var req = new ListMyOutgoingSharesAllRequest { Limit = limit is > 0 and <= 200 ? limit.Value : 60 };
                if (DateTimeOffset.TryParse(cursorAt, out var dt))
                    req.CursorSharedAt = Timestamp.FromDateTimeOffset(dt.ToUniversalTime());
                if (!string.IsNullOrEmpty(cursorId))
                    req.CursorGrantId = cursorId;

                var resp = await cloud.ListMyOutgoingSharesAllAsync(req, token);
                var byId = await ResolveUsers(usersServer, resp.Items.Select(i => i.RecipientUserId));
                return Results.Json(new
                {
                    items = resp.Items.Select(i => new
                    {
                        grantId = i.GrantId,
                        file = CloudJson.Media(i.File),
                        sharedAt = i.SharedAt?.ToDateTimeOffset(),
                        recipient = byId.TryGetValue(i.RecipientUserId, out var u) ? UserJson(u) : MinimalUserJson(i.RecipientUserId)
                    }).ToArray(),
                    nextCursorAt = resp.NextCursorSharedAt?.ToDateTimeOffset(),
                    nextCursorId = resp.NextCursorGrantId
                }, Json);
            }));

        // Доступные мне файлы (раздел «мне доступны»).
        api.MapGet("/shared/with-me", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud,
            UsersServerApi.UsersServerApiClient usersServer, int? limit, string? cursorAt, string? cursorId) =>
            await Guarded(http, auth, async token =>
            {
                var req = new ListSharedWithMeRequest { Limit = limit is > 0 and <= 200 ? limit.Value : 60 };
                if (DateTimeOffset.TryParse(cursorAt, out var dt))
                    req.CursorSharedAt = Timestamp.FromDateTimeOffset(dt.ToUniversalTime());
                if (!string.IsNullOrEmpty(cursorId))
                    req.CursorGrantId = cursorId;

                var resp = await cloud.ListSharedWithMeAsync(req, token);
                var byId = await ResolveUsers(usersServer, resp.Items.Select(i => i.OwnerUserId));
                return Results.Json(new
                {
                    items = resp.Items.Select(i => new
                    {
                        grantId = i.GrantId,
                        file = CloudJson.Media(i.File),
                        sharedAt = i.SharedAt?.ToDateTimeOffset(),
                        owner = byId.TryGetValue(i.OwnerUserId, out var u) ? UserJson(u) : MinimalUserJson(i.OwnerUserId)
                    }).ToArray(),
                    nextCursorAt = resp.NextCursorSharedAt?.ToDateTimeOffset(),
                    nextCursorId = resp.NextCursorGrantId
                }, Json);
            }));

        // Временная ссылка на скачивание доступного мне файла (grant-проверка на сервере).
        api.MapPost("/shared/download", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, FileIdReq body) =>
            await Guarded(http, auth, async token =>
            {
                var resp = await cloud.GetSharedFileDownloadUrlAsync(new GetSharedFileDownloadUrlRequest { FileId = body.FileId }, token);
                return Results.Json(new { downloadUrl = resp.DownloadUrl }, Json);
            }));

        // ───────────────────────── Шаринг папок между пользователями ─────────────────────────

        // Поделиться папкой с пользователем.
        api.MapPost("/shared/grant-folder", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, GrantFolderReq body) =>
            await Guarded(http, auth, async token =>
            {
                await cloud.ShareFolderWithUserAsync(
                    new ShareFolderWithUserRequest { DirectoryId = body.DirectoryId, RecipientUserId = body.RecipientUserId }, token);
                return Results.Json(new { ok = true }, Json);
            }));

        // Отозвать грант доступа к папке.
        api.MapPost("/shared/revoke-folder-grant", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, GrantIdReq body) =>
            await Guarded(http, auth, async token =>
            {
                await cloud.RevokeFolderUserShareAsync(new RevokeFolderUserShareRequest { GrantId = body.GrantId }, token);
                return Results.Json(new { ok = true }, Json);
            }));

        // Я поделился (папки): какие папки и кому я отдал.
        api.MapGet("/shared/i-shared-folders", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud,
            UsersServerApi.UsersServerApiClient usersServer) =>
            await Guarded(http, auth, async token =>
            {
                var resp = await cloud.ListMyOutgoingFolderSharesAsync(new ListMyOutgoingFolderSharesRequest(), token);
                var byId = await ResolveUsers(usersServer, resp.Items.Select(i => i.RecipientUserId));
                return Results.Json(new
                {
                    items = resp.Items.Select(i => new
                    {
                        grantId = i.GrantId,
                        directoryId = i.DirectoryId,
                        name = i.Name,
                        sharedAt = i.SharedAt?.ToDateTimeOffset(),
                        recipient = byId.TryGetValue(i.RecipientUserId, out var u) ? UserJson(u) : MinimalUserJson(i.RecipientUserId)
                    }).ToArray()
                }, Json);
            }));

        // Мне доступны (папки): какие папки расшарили мне.
        api.MapGet("/shared/folders-with-me", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud,
            UsersServerApi.UsersServerApiClient usersServer) =>
            await Guarded(http, auth, async token =>
            {
                var resp = await cloud.ListSharedFoldersWithMeAsync(new ListSharedFoldersWithMeRequest(), token);
                var byId = await ResolveUsers(usersServer, resp.Items.Select(i => i.OwnerUserId));
                return Results.Json(new
                {
                    items = resp.Items.Select(i => new
                    {
                        grantId = i.GrantId,
                        directoryId = i.DirectoryId,
                        name = i.Name,
                        sharedAt = i.SharedAt?.ToDateTimeOffset(),
                        owner = byId.TryGetValue(i.OwnerUserId, out var u) ? UserJson(u) : MinimalUserJson(i.OwnerUserId)
                    }).ToArray()
                }, Json);
            }));

        // Листинг доступной мне папки (навигация по поддереву; проверка гранта на сервере).
        api.MapGet("/shared/dir", async (HttpContext http, AuthGateway auth, CloudApi.CloudApiClient cloud, string dir) =>
            await Guarded(http, auth, async token =>
            {
                var resp = await cloud.ListSharedDirectoryAsync(new ListSharedDirectoryRequest { DirectoryId = dir }, token);
                if (!resp.Found)
                    return Results.Json(new { found = false }, Json, statusCode: 404);

                return Results.Json(new
                {
                    found = true,
                    directoryId = resp.DirectoryId,
                    name = resp.Name,
                    subdirs = resp.Subdirs.Select(d => new { id = d.Id, name = d.Name }).ToArray(),
                    files = resp.Files.Select(f => new
                    {
                        fileId = f.FileId,
                        name = f.Name,
                        mediaKind = f.MediaKind.ToString().ToLowerInvariant(),
                        downloadUrl = f.DownloadUrl,
                        previewUrl = f.PreviewUrl,
                        fileSize = f.FileSize,
                        imageWidth = f.ImageWidth,
                        imageHeight = f.ImageHeight
                    }).ToArray()
                }, Json);
            }));

        // ───────────────────────── Альбомы ─────────────────────────

        api.MapGet("/albums", async (HttpContext http, AuthGateway auth, AlbumApi.AlbumApiClient albums,
            int? limit, string? cursorAt, string? cursorId) =>
            await Guarded(http, auth, async token =>
            {
                var req = new ListAlbumsRequest { Limit = limit is > 0 and <= 200 ? limit.Value : 60 };
                if (DateTimeOffset.TryParse(cursorAt, out var dt))
                    req.CursorUpdatedAt = Timestamp.FromDateTimeOffset(dt.ToUniversalTime());
                if (!string.IsNullOrEmpty(cursorId))
                    req.CursorAlbumId = cursorId;

                var resp = await albums.ListAlbumsAsync(req, token);
                return Results.Json(new
                {
                    albums = resp.Albums.Select(CloudJson.Album).ToArray(),
                    nextCursorAt = resp.NextCursorUpdatedAt?.ToDateTimeOffset(),
                    nextCursorId = resp.NextCursorAlbumId
                }, Json);
            }));

        api.MapGet("/albums/items", async (HttpContext http, AuthGateway auth, AlbumApi.AlbumApiClient albums,
            string album, string? kind, int? limit, string? cursorAt, string? cursorId) =>
            await Guarded(http, auth, async token =>
            {
                var req = new ListAlbumItemsRequest
                {
                    AlbumId = album,
                    Limit = limit is > 0 and <= 200 ? limit.Value : 100
                };
                if (kind == "photo") req.KindFilter = MediaKind.Photo;
                else if (kind == "video") req.KindFilter = MediaKind.Video;
                if (DateTimeOffset.TryParse(cursorAt, out var dt))
                    req.CursorAddedAt = Timestamp.FromDateTimeOffset(dt.ToUniversalTime());
                if (!string.IsNullOrEmpty(cursorId))
                    req.CursorFileId = cursorId;

                var resp = await albums.ListAlbumItemsAsync(req, token);
                return Results.Json(new
                {
                    items = resp.Items.Where(i => i.File is not null).Select(i => CloudJson.Media(i.File)).ToArray(),
                    nextCursorAt = resp.NextCursorAddedAt?.ToDateTimeOffset(),
                    nextCursorId = resp.NextCursorFileId
                }, Json);
            }));

        api.MapPost("/albums", async (HttpContext http, AuthGateway auth, AlbumApi.AlbumApiClient albums, AlbumCreate body) =>
            await Guarded(http, auth, async token =>
            {
                var info = await albums.CreateAlbumAsync(
                    new CreateAlbumRequest { Name = body.Name, Description = body.Description ?? "" }, token);
                return Results.Json(CloudJson.Album(info), Json);
            }));

        api.MapPost("/albums/update", async (HttpContext http, AuthGateway auth, AlbumApi.AlbumApiClient albums, AlbumUpdate body) =>
            await Guarded(http, auth, async token =>
            {
                var req = new UpdateAlbumRequest { AlbumId = body.Album };
                if (body.Name is not null) req.Name = body.Name;
                if (body.Description is not null) req.Description = body.Description;
                if (body.CoverFileId is not null) req.CoverFileId = body.CoverFileId;
                var info = await albums.UpdateAlbumAsync(req, token);
                return Results.Json(CloudJson.Album(info), Json);
            }));

        api.MapPost("/albums/delete", async (HttpContext http, AuthGateway auth, AlbumApi.AlbumApiClient albums, AlbumIdReq body) =>
            await Guarded(http, auth, async token =>
            {
                await albums.DeleteAlbumAsync(new DeleteAlbumRequest { AlbumId = body.Album }, token);
                return Results.Json(new { ok = true }, Json);
            }));

        api.MapPost("/albums/items/add", async (HttpContext http, AuthGateway auth, AlbumApi.AlbumApiClient albums, AlbumItems body) =>
            await Guarded(http, auth, async token =>
            {
                var req = new AddItemsToAlbumRequest { AlbumId = body.Album };
                req.FileIds.AddRange(body.FileIds ?? []);
                await albums.AddItemsToAlbumAsync(req, token);
                return Results.Json(new { ok = true }, Json);
            }));

        api.MapPost("/albums/items/remove", async (HttpContext http, AuthGateway auth, AlbumApi.AlbumApiClient albums, AlbumItems body) =>
            await Guarded(http, auth, async token =>
            {
                var req = new RemoveItemsFromAlbumRequest { AlbumId = body.Album };
                req.FileIds.AddRange(body.FileIds ?? []);
                await albums.RemoveItemsFromAlbumAsync(req, token);
                return Results.Json(new { ok = true }, Json);
            }));

        // ───────────────────────── Умные (динамические) папки ─────────────────────────

        api.MapGet("/dynamic-folders", async (HttpContext http, AuthGateway auth, DynamicFolderApi.DynamicFolderApiClient folders) =>
            await Guarded(http, auth, async token =>
            {
                var resp = await folders.ListDynamicFoldersAsync(new ListDynamicFoldersRequest(), token);
                return Results.Json(new { folders = resp.Folders.Select(CloudJson.DynamicFolder).ToArray() }, Json);
            }));

        api.MapGet("/dynamic-folders/items", async (HttpContext http, AuthGateway auth, DynamicFolderApi.DynamicFolderApiClient folders,
            string folder, string? kind, int? limit, string? cursorAt, string? cursorId) =>
            await Guarded(http, auth, async token =>
            {
                var req = new ListDynamicFolderItemsRequest
                {
                    FolderId = folder,
                    Limit = limit is > 0 and <= 200 ? limit.Value : 100
                };
                if (kind == "photo") req.KindFilter = MediaKind.Photo;
                else if (kind == "video") req.KindFilter = MediaKind.Video;
                if (DateTimeOffset.TryParse(cursorAt, out var dt))
                    req.CursorCreatedAt = Timestamp.FromDateTimeOffset(dt.ToUniversalTime());
                if (!string.IsNullOrEmpty(cursorId))
                    req.CursorFileId = cursorId;

                var resp = await folders.ListDynamicFolderItemsAsync(req, token);
                return Results.Json(new
                {
                    items = resp.Items.Select(CloudJson.MediaItem).ToArray(),
                    nextCursorAt = resp.NextCursorCreatedAt?.ToDateTimeOffset(),
                    nextCursorId = resp.NextCursorFileId
                }, Json);
            }));

        api.MapPost("/dynamic-folders", async (HttpContext http, AuthGateway auth, DynamicFolderApi.DynamicFolderApiClient folders, DfCreate body) =>
            await Guarded(http, auth, async token =>
            {
                var req = new CreateDynamicFolderRequest
                {
                    Name = body.Name,
                    Combinator = (DfCombinator)body.Combinator,
                    IconKey = body.IconKey ?? "",
                    CoverColor = body.CoverColor ?? "",
                    ViewMode = (DfViewMode)(body.ViewMode ?? 0)
                };
                if (body.Rules is not null)
                    req.Rules.AddRange(body.Rules.Select(ToProtoRule));
                var info = await folders.CreateDynamicFolderAsync(req, token);
                return Results.Json(CloudJson.DynamicFolder(info), Json);
            }));

        api.MapPost("/dynamic-folders/update", async (HttpContext http, AuthGateway auth, DynamicFolderApi.DynamicFolderApiClient folders, DfUpdate body) =>
            await Guarded(http, auth, async token =>
            {
                var req = new UpdateDynamicFolderRequest
                {
                    FolderId = body.Folder,
                    Combinator = (DfCombinator)body.Combinator
                };
                if (body.Name is not null) req.Name = body.Name;
                if (body.IconKey is not null) req.IconKey = body.IconKey;
                if (body.CoverColor is not null) req.CoverColor = body.CoverColor;
                if (body.ViewMode is not null) req.ViewMode = (DfViewMode)body.ViewMode.Value;
                if (body.Rules is not null)
                    req.Rules.AddRange(body.Rules.Select(ToProtoRule));
                var info = await folders.UpdateDynamicFolderAsync(req, token);
                return Results.Json(CloudJson.DynamicFolder(info), Json);
            }));

        api.MapPost("/dynamic-folders/delete", async (HttpContext http, AuthGateway auth, DynamicFolderApi.DynamicFolderApiClient folders, DfIdReq body) =>
            await Guarded(http, auth, async token =>
            {
                await folders.DeleteDynamicFolderAsync(new DeleteDynamicFolderRequest { FolderId = body.Folder }, token);
                return Results.Json(new { ok = true }, Json);
            }));

        // ───────────────────────── Файлы: загрузка / оригинал ─────────────────────────

        // Проверка наличия по SHA256-хешу (без побочных эффектов): клиент считает хеш в браузере
        // и, если контент уже есть, показывает модалку «такой файл уже есть» с его именем и папкой.
        api.MapPost("/files/check-hash", async (HttpContext http, AuthGateway auth, FilesApi.FilesApiClient files, HashReq body) =>
            await Guarded(http, auth, async token =>
            {
                var resp = await files.CheckFileHashAsync(new CheckFileHashRequest { FileHash = body.Hash ?? "" }, token);
                return Results.Json(new
                {
                    fileId = resp.FileId,
                    exists = resp.Exists,
                    locations = resp.ExistingLocations.Select(l => new
                    {
                        entryId = l.EntryId,
                        name = l.Name,
                        directoryId = l.DirectoryId,
                        directoryName = l.DirectoryName
                    })
                }, Json);
            }));

        // Прокси-загрузка: получаем upload-URL у Files и стримим туда байты (same-origin, без CORS).
        // Байты льём на ВНУТРЕННИЙ HTTP1-эндпоинт Files (минуя nginx/TLS); публичный upload.Url — fallback.
        api.MapPost("/files/upload", async (HttpContext http, AuthGateway auth, FilesApi.FilesApiClient files, IHttpClientFactory httpFactory, IConfiguration config) =>
            await Guarded(http, auth, async (user, _) =>
            {
                var form = await http.Request.ReadFormAsync();
                var file = form.Files["file"];
                if (file is null || file.Length == 0)
                    return Results.BadRequest(new { error = "Файл не выбран или пустой." });

                var device = BrowserContext.BuildDeviceInfo(
                    http,
                    user.DeviceId ?? auth.GetOrCreateDeviceId(http),
                    config.Value("App:AppName", "BarkCloud Web"),
                    config.Value("App:Version", "v1.0.0"));
                var uploadToken = BrowserContext.UserTokenWithDevice(user.AccessToken, device);
                var upload = await files.GetUploadUrlAsync(new GetUploadUrlRequest { FileType = UploadFileType.CloudFile }, uploadToken);

                var http1Base = config["FilesService:Http1Base"];
                var uploadUrl = string.IsNullOrEmpty(http1Base) ? upload.Url : $"{http1Base}/upload/{upload.FileId}";

                using var content = new MultipartFormDataContent();
                var part = new StreamContent(file.OpenReadStream());
                part.Headers.ContentType = new MediaTypeHeaderValue(
                    string.IsNullOrEmpty(file.ContentType) ? "application/octet-stream" : file.ContentType);
                content.Add(part, "file", file.FileName);

                var client = httpFactory.CreateClient("files-upload");
                using var resp = await client.PostAsync(uploadUrl, content);
                var responseBody = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                    return Results.Json(new { error = responseBody }, Json, statusCode: (int)resp.StatusCode);

                // ответ upload: { "fileId": "<guid>" } — теперь всегда равен запрошенному (серверный дедуп снят)
                string? fileId = null;
                try
                {
                    using var doc = JsonDocument.Parse(responseBody);
                    if (doc.RootElement.TryGetProperty("fileId", out var fid))
                        fileId = fid.GetString();
                }
                catch (JsonException) { /* ниже отдадим ошибку */ }

                if (string.IsNullOrEmpty(fileId))
                    return Results.Json(new { error = "Не удалось разобрать ответ загрузки." }, Json, statusCode: 502);

                return Results.Json(new { fileId, name = file.FileName }, Json);
            })).DisableAntiforgery();

        // Временная ссылка(и) на оригинал(ы) для просмотра/скачивания.
        api.MapGet("/files/download", async (HttpContext http, AuthGateway auth, FilesApi.FilesApiClient files, string ids) =>
            await Guarded(http, auth, async token =>
            {
                var idList = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var req = new GetTempDownloadUrlRequest();
                req.FileIds.AddRange(idList);

                var resp = await files.GetTempDownloadUrlAsync(req, token);
                return Results.Json(new
                {
                    urls = resp.FileUrls.ToDictionary(f => f.FileId, f => f.Url)
                }, Json);
            }));

        // Полные свойства файла по file_id (для модалки «Свойства») — через серверный GetFileData.
        // FilesServerApi авторизуется сервисным токеном (интерцептор), поэтому проверяем владение вручную.
        // Дополнительно подтягиваем EXIF/ffprobe/PDF/Office-метаданные через клиентский GetFileMetadata
        // (юзер-токен → авторизация по Uploaders на стороне Files).
        api.MapGet("/files/info", async (
            HttpContext http,
            AuthGateway auth,
            FilesServerApi.FilesServerApiClient filesServer,
            FilesApi.FilesApiClient filesUser,
            string? id) =>
        {
            var user = await auth.AuthenticateAsync(http);
            if (user is null)
                return Results.Json(new { error = "Не авторизован" }, Json, statusCode: 401);
            if (string.IsNullOrEmpty(id))
                return Results.Json(new { error = "Не указан id" }, Json, statusCode: 400);

            try
            {
                var resp = await filesServer.GetFileDataAsync(new GetFileDataRequest { FileId = id });
                var f = resp.FileInfo;
                if (f is null || string.IsNullOrEmpty(f.Id))
                    return Results.Json(new { error = "Файл не найден" }, Json, statusCode: 404);
                if (!f.Uploaders.Contains(user.UserId))
                    return Results.Json(new { error = "Нет доступа" }, Json, statusCode: 403);

                object? metadataJson = null;
                try
                {
                    var metaResp = await filesUser.GetFileMetadataAsync(
                        new GetFileMetadataRequest { FileId = id },
                        BrowserContext.UserToken(user.AccessToken));
                    if (metaResp.HasMetadata && metaResp.Metadata is not null)
                        metadataJson = FileMetadataJson(metaResp.Metadata);
                }
                catch (RpcException)
                {
                    // Метаданных может ещё не быть (бэкафилл не дошёл) — это не ошибка модалки.
                }

                var (iconKind, ext) = FileKind.Classify(f.FileName);
                return Results.Json(new
                {
                    id = f.Id,
                    name = f.FileName,
                    ext,
                    iconKind,
                    kind = f.MediaKind switch
                    {
                        MediaKind.Photo => "photo",
                        MediaKind.Video => "video",
                        MediaKind.Document => "document",
                        MediaKind.Audio => "audio",
                        _ => "other"
                    },
                    size = f.FileSize,
                    sizeLabel = Format.Size(f.FileSize),
                    width = f.ImageWidth,
                    height = f.ImageHeight,
                    etag = f.Etag,
                    previewCount = f.Previews.Count,
                    createdAt = f.CreatedAt?.ToDateTimeOffset(),
                    uploadedAt = f.UploadedAt?.ToDateTimeOffset(),
                    uploadDeviceName = f.UploadDeviceName,
                    metadata = metadataJson
                }, Json);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                return Results.Json(new { error = "Файл не найден" }, Json, statusCode: 404);
            }
            catch (RpcException ex)
            {
                return Results.Json(new { error = ex.Status.Detail }, Json, statusCode: 502);
            }
        });
    }

    // ───────────────────────── Инфраструктура ─────────────────────────

    private static string ResolveOrigin(HttpContext http)
    {
        var publicHost = http.RequestServices.GetRequiredService<IConfiguration>()["App:PublicHost"];
        if (!string.IsNullOrWhiteSpace(publicHost) && Uri.TryCreate(publicHost, UriKind.Absolute, out var uri))
            return $"{uri.Scheme}://{uri.Authority}";
        return $"{http.Request.Scheme}://{http.Request.Host}";
    }

    /// <summary>
    /// JSON-представление публичной ссылки. Дружелюбный URL собирается из App:PublicHost
    /// (если задан — включает порт) либо из хоста текущего запроса.
    /// </summary>
    private static object ShareJson(HttpContext http, ShareInfo s)
    {
        var origin = ResolveOrigin(http);
        return new
        {
            id = s.Id,
            token = s.Token,
            url = $"{origin}/v/{s.Token}",
            downloadUrl = $"{origin}/s/{s.Token}",
            fileId = s.FileId,
            name = s.Name,
            createdAt = s.CreatedAt?.ToDateTimeOffset(),
            clickCount = s.ClickCount,
            mediaKind = CloudJson.MediaKindName(s.MediaKind),
            previewUrl = s.PreviewUrl
        };
    }

    /// <summary>JSON-представление публичной папки. URL `/f/{token}` собирается из App:PublicHost либо хоста запроса.</summary>
    private static object FolderShareJson(HttpContext http, FolderShareInfo s)
    {
        var origin = ResolveOrigin(http);
        return new
        {
            id = s.Id,
            token = s.Token,
            kind = "folder",
            url = $"{origin}/f/{s.Token}",
            directoryId = s.DirectoryId,
            name = s.Name,
            createdAt = s.CreatedAt?.ToDateTimeOffset(),
            clickCount = s.ClickCount
        };
    }

    /// <summary>JSON-представление публичного альбома. URL `/al/{token}` собирается из App:PublicHost либо хоста запроса.</summary>
    private static object AlbumShareJson(HttpContext http, AlbumShareInfo s)
    {
        var origin = ResolveOrigin(http);
        return new
        {
            id = s.Id,
            token = s.Token,
            kind = "album",
            url = $"{origin}/al/{s.Token}",
            albumId = s.AlbumId,
            name = s.Name,
            createdAt = s.CreatedAt?.ToDateTimeOffset(),
            clickCount = s.ClickCount
        };
    }

    private static object UserJson(User u) => new
    {
        id = u.Id,
        username = u.Username,
        firstName = u.FirstName,
        lastName = u.LastName,
        avatar = u.ProfilePicturePreview
    };

    private static object MinimalUserJson(long id) => new { id, username = "", firstName = "", lastName = "", avatar = "" };

    /// <summary>Резолв id пользователей → User через UsersServerApi (имена «от кого / кому»).</summary>
    private static async Task<Dictionary<long, User>> ResolveUsers(
        UsersServerApi.UsersServerApiClient usersServer, IEnumerable<long> ids)
    {
        var distinct = ids.Distinct().ToList();
        if (distinct.Count == 0)
            return new Dictionary<long, User>();

        var req = new ListByIdsRequest();
        req.Ids.AddRange(distinct);
        var resp = await usersServer.ListByIdsAsync(req);
        return resp.Users
            .GroupBy(u => u.Id)
            .ToDictionary(g => g.Key, g => g.First());
    }

    private static async Task<object> RunIdBatch(string[]? rawIds, Func<string, Task> action)
    {
        var invalid = new List<string>();
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in rawIds ?? Array.Empty<string>())
        {
            if (!Guid.TryParse(raw, out var id))
            {
                invalid.Add(raw);
                continue;
            }

            var normalized = id.ToString();
            if (seen.Add(normalized))
                ids.Add(normalized);
        }

        var succeeded = 0;
        var succeededIds = new List<string>();
        var failedIds = new List<string>(invalid);
        foreach (var id in ids)
        {
            try
            {
                await action(id);
                succeeded++;
                succeededIds.Add(id);
            }
            catch (RpcException ex) when (ex.StatusCode is StatusCode.FailedPrecondition or StatusCode.NotFound or StatusCode.PermissionDenied)
            {
                // Частичная ошибка одного элемента не должна валить всю batch-операцию.
                failedIds.Add(id);
            }
        }

        var total = ids.Count + invalid.Count;
        return new
        {
            total,
            succeeded,
            failed = total - succeeded,
            invalidIds = invalid.ToArray(),
            succeededIds = succeededIds.ToArray(),
            failedIds = failedIds.ToArray()
        };
    }

    /// <summary>
    /// gRPC <see cref="FileMetadataInfo"/> → плоский JSON для модалки «Свойства».
    /// Все поля опциональны — отдаём только те, что заданы (через HasFoo), без «пустых» нулей.
    /// </summary>
    private static object FileMetadataJson(FileMetadataInfo m)
    {
        var dict = new Dictionary<string, object?>();

        if (m.TakenAt is not null) dict["takenAt"] = m.TakenAt.ToDateTimeOffset();
        if (m.HasCreatorTool) dict["creatorTool"] = m.CreatorTool;

        if (m.HasLatitude) dict["latitude"] = m.Latitude;
        if (m.HasLongitude) dict["longitude"] = m.Longitude;
        if (m.HasAltitude) dict["altitude"] = m.Altitude;

        if (m.HasCameraMake) dict["cameraMake"] = m.CameraMake;
        if (m.HasCameraModel) dict["cameraModel"] = m.CameraModel;
        if (m.HasLensModel) dict["lensModel"] = m.LensModel;

        if (m.HasFocalLengthMm) dict["focalLengthMm"] = m.FocalLengthMm;
        if (m.HasFNumber) dict["fNumber"] = m.FNumber;
        if (m.HasExposureTimeSeconds) dict["exposureTimeSeconds"] = m.ExposureTimeSeconds;
        if (m.HasIso) dict["iso"] = m.Iso;
        if (m.HasOrientation) dict["orientation"] = m.Orientation;
        if (m.HasFlash) dict["flash"] = m.Flash;

        if (m.HasDurationSeconds) dict["durationSeconds"] = m.DurationSeconds;
        if (m.HasVideoCodec) dict["videoCodec"] = m.VideoCodec;
        if (m.HasAudioCodec) dict["audioCodec"] = m.AudioCodec;
        if (m.HasBitrate) dict["bitrate"] = m.Bitrate;
        if (m.HasFrameRate) dict["frameRate"] = m.FrameRate;

        if (m.HasDocumentAuthor) dict["documentAuthor"] = m.DocumentAuthor;
        if (m.HasDocumentTitle) dict["documentTitle"] = m.DocumentTitle;
        if (m.HasDocumentSubject) dict["documentSubject"] = m.DocumentSubject;
        if (m.HasDocumentPageCount) dict["documentPageCount"] = m.DocumentPageCount;

        return dict;
    }

    /// <summary>Авторизация по cookie + единая обработка gRPC-ошибок.</summary>
    private static async Task<IResult> Guarded(HttpContext http, AuthGateway auth, Func<Metadata, Task<IResult>> action)
        => await Guarded(http, auth, async (_, token) => await action(token));

    /// <summary>Авторизация по cookie + единая обработка gRPC-ошибок.</summary>
    private static async Task<IResult> Guarded(HttpContext http, AuthGateway auth, Func<WebUser, Metadata, Task<IResult>> action)
    {
        var user = await auth.AuthenticateAsync(http);
        if (user is null)
            return Results.Json(new { error = "Не авторизован" }, Json, statusCode: 401);

        try
        {
            return await action(user, BrowserContext.UserToken(user.AccessToken));
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.FailedPrecondition)
        {
            // доменная ошибка: ErrorCode (GUID) в trailing-метадате
            var code = ex.Trailers.GetValue("x-error-code");
            return Results.Json(new { error = ex.Status.Detail, code }, Json, statusCode: 400);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
        {
            return Results.Json(new { error = "Не авторизован" }, Json, statusCode: 401);
        }
        catch (RpcException ex)
        {
            return Results.Json(new { error = ex.Status.Detail }, Json, statusCode: 502);
        }
    }

    // ───────────────────────── DTO тел запросов ─────────────────────────

    private sealed record DirCreate(string? ParentId, string Name);
    private sealed record RenameReq(string Id, string Name);
    private sealed record MoveReq(string Id, string? ParentId);
    private sealed record IdReq(string Id);
    private sealed record AttachReq(string? Dir, string FileId, string Name, bool RouteByMediaKind = false);
    private sealed record EntryRenameReq(string EntryId, string Name);
    private sealed record EntryMoveReq(string EntryId, string? Dir);
    private sealed record EntryIdReq(string EntryId);
    private sealed record EntryIdsReq(string[]? EntryIds);
    private sealed record FileIdReq(string FileId);
    private sealed record FileIdsReq(string[]? FileIds);
    private sealed record GrantReq(string FileId, long RecipientUserId);
    private sealed record GrantFolderReq(string DirectoryId, long RecipientUserId);
    private sealed record GrantIdReq(string GrantId);
    private sealed record AlbumCreate(string Name, string? Description);
    private sealed record AlbumUpdate(string Album, string? Name, string? Description, string? CoverFileId);
    private sealed record AlbumIdReq(string Album);
    private sealed record AlbumItems(string Album, string[]? FileIds);
    private sealed record DfRuleDto(int Field, int Op, string? Value);
    private sealed record DfCreate(string Name, int Combinator, DfRuleDto[]? Rules, string? IconKey, string? CoverColor, int? ViewMode);
    private sealed record DfUpdate(string Folder, int Combinator, DfRuleDto[]? Rules, string? Name, string? IconKey, string? CoverColor, int? ViewMode);
    private sealed record DfIdReq(string Folder);

    private static DfRule ToProtoRule(DfRuleDto r) => new()
    {
        Field = (DfField)r.Field,
        Operator = (DfOperator)r.Op,
        Value = r.Value ?? ""
    };
    private sealed record HashReq(string? Hash);
    private sealed record VideoThumbReq(string VideoFileId, string ImageFileId);
    private sealed record ShareCreateReq(string FileId, string? Name);
    private sealed record ShareIdReq(string ShareId);
    private sealed record FolderShareCreateReq(string DirectoryId, string? Name);
    private sealed record FolderShareIdReq(string FolderShareId);
    private sealed record AlbumShareCreateReq(string AlbumId, string? Name);
    private sealed record AlbumShareIdReq(string AlbumShareId);
}
