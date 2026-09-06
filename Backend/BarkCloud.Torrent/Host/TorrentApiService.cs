using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Torrent;
using BarkCloud.Shared.Identity;
using BarkCloud.Torrent.Domain;
using BarkCloud.Torrent.Infrastructure;
using BarkCloud.Torrent.Persistence;

using Grpc.Core;

using Microsoft.AspNetCore.Authorization;

using MonoTorrent;

using System.Text;

namespace BarkCloud.Torrent.Host;

[Authorize(Policy = nameof(TokenType.User))]
public class TorrentApiService : TorrentApi.TorrentApiBase
{
    private readonly UserContext _userContext;
    private readonly ITorrentStore _store;
    private readonly TorrentEngineService _engine;
    private readonly TorrentImportService _import;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly MetricsCollector _metrics;

    public TorrentApiService(
        UserContext userContext,
        ITorrentStore store,
        TorrentEngineService engine,
        TorrentImportService import,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        MetricsCollector metrics)
    {
        _userContext = userContext;
        _store = store;
        _engine = engine;
        _import = import;
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _metrics = metrics;
    }

    private long UserId => _userContext.UserId;

    private string UserSavePath =>
        Path.Combine(_configuration["Torrent:DownloadPath"] ?? "/mnt/torrents", UserId.ToString());

    public override async Task<TorrentInfo> AddMagnet(AddMagnetRequest request, ServerCallContext context)
    {
        var link = MagnetLink.Parse(request.MagnetUri);
        var infoHash = link.InfoHashes.V1OrV2.ToHex();
        if (await _store.ExistsByInfoHash(UserId, infoHash))
            throw new RpcException(new Status(StatusCode.AlreadyExists, "Торрент с таким infohash уже добавлен"));

        var id = Guid.NewGuid();
        var entity = new TorrentEntity
        {
            Id = id,
            UserId = UserId,
            InfoHash = link.InfoHashes.V1OrV2.ToHex(),
            Name = link.Name ?? "",
            MagnetUri = request.MagnetUri,
            SavePath = UserSavePath,
            Status = (int)TorrentStatus.Metadata,
            AddedAt = DateTime.UtcNow,
        };

        await _store.Add(entity);
        try
        {
            var managed = await _engine.AddMagnetAsync(id, request.MagnetUri, entity.SavePath, start: true);
            _metrics.Increment("torrents_added");
            return TorrentMapper.ToInfo(entity, managed);
        }
        catch (DuplicateTorrentException ex)
        {
            await _store.Remove(entity);
            throw new RpcException(new Status(StatusCode.AlreadyExists, ex.Message));
        }
        catch
        {
            await _store.Remove(entity);
            throw;
        }
    }

    public override async Task<TorrentInfo> AddTorrentFile(AddTorrentFileRequest request, ServerCallContext context)
    {
        var bytes = request.TorrentFile.ToByteArray();
        var torrent = await MonoTorrent.Torrent.LoadAsync(bytes);

        var infoHash = torrent.InfoHashes.V1OrV2.ToHex();
        if (await _store.ExistsByInfoHash(UserId, infoHash))
            throw new RpcException(new Status(StatusCode.AlreadyExists, "Торрент с таким infohash уже добавлен"));

        var id = Guid.NewGuid();
        var entity = new TorrentEntity
        {
            Id = id,
            UserId = UserId,
            InfoHash = torrent.InfoHashes.V1OrV2.ToHex(),
            Name = torrent.Name,
            TorrentFile = bytes,
            SavePath = UserSavePath,
            TotalSize = torrent.Size,
            Status = (int)TorrentStatus.Downloading,
            AddedAt = DateTime.UtcNow,
        };

        for (var i = 0; i < torrent.Files.Count; i++)
        {
            entity.Files.Add(new TorrentFileEntity
            {
                Id = Guid.NewGuid(),
                Index = i,
                Path = torrent.Files[i].Path.ToString(),
                Size = torrent.Files[i].Length,
                Priority = (int)TorrentFilePriority.Normal,
            });
        }

        await _store.Add(entity);
        try
        {
            var managed = await _engine.AddTorrentFileAsync(id, bytes, entity.SavePath, start: true);
            _metrics.Increment("torrents_added");
            return TorrentMapper.ToInfo(entity, managed);
        }
        catch (DuplicateTorrentException ex)
        {
            await _store.Remove(entity);
            throw new RpcException(new Status(StatusCode.AlreadyExists, ex.Message));
        }
        catch
        {
            await _store.Remove(entity);
            throw;
        }
    }

    public override async Task<ListTorrentsResponse> ListTorrents(ListTorrentsRequest request, ServerCallContext context)
    {
        var entities = await _store.ListByUser(UserId);
        var response = new ListTorrentsResponse();
        foreach (var e in entities)
            response.Torrents.Add(TorrentMapper.ToInfo(e, _engine.Get(e.Id)));
        return response;
    }

    public override async Task<SearchTorrentsResponse> SearchTorrents(SearchTorrentsRequest request, ServerCallContext context)
    {
        var query = (request.Query ?? string.Empty).Trim().ToLowerInvariant();
        var limit = request.Limit is > 0 and <= 50 ? request.Limit : 20;
        if (query.Length < 2)
            return new SearchTorrentsResponse();

        var rows = (await _store.SearchByUser(UserId, query, context.CancellationToken))
            .Select(entity => new { Entity = entity, Score = SearchRank(entity.Name, entity.InfoHash, query) })
            .OrderByDescending(x => x.Score.Rank)
            .ThenByDescending(x => x.Score.Similarity)
            .ThenByDescending(x => x.Entity.AddedAt)
            .ThenByDescending(x => x.Entity.Id)
            .ToList();
        var start = 0;
        if (!string.IsNullOrEmpty(request.Cursor))
        {
            var cursor = DecodeCursor(request.Cursor);
            var cursorIndex = rows.FindIndex(x => x.Score.Rank == cursor.Rank
                && x.Entity.AddedAt.Ticks == cursor.AddedAtTicks
                && x.Entity.Id == cursor.Id);
            if (cursorIndex < 0)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Некорректный курсор."));
            start = cursorIndex + 1;
        }
        var page = rows.Skip(start).Take(limit).ToList();
        var hasMore = rows.Count > start + page.Count;

        var response = new SearchTorrentsResponse { HasMore = hasMore };
        foreach (var row in page)
            response.Torrents.Add(TorrentMapper.ToInfo(row.Entity, _engine.Get(row.Entity.Id)));
        if (page.Count > 0)
            response.NextCursor = EncodeCursor(page[^1].Entity, page[^1].Score.Rank);
        return response;
    }

    private static string EncodeCursor(TorrentEntity entity, int rank)
    {
        var value = $"{rank}|{entity.AddedAt.Ticks}|{entity.Id:N}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static (int Rank, long AddedAtTicks, Guid Id) DecodeCursor(string value)
    {
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(base64)).Split('|');
            if (parts.Length == 3
                && int.TryParse(parts[0], out var rank)
                && long.TryParse(parts[1], out var ticks)
                && Guid.TryParseExact(parts[2], "N", out var id))
                return (rank, ticks, id);
        }
        catch (FormatException)
        {
            // Некорректное внешнее значение ниже будет преобразовано в InvalidArgument.
        }

        throw new RpcException(new Status(StatusCode.InvalidArgument, "Некорректный курсор."));
    }

    private static (int Rank, double Similarity) SearchRank(string name, string infoHash, string query)
    {
        var normalizedName = name.ToLowerInvariant();
        var normalizedHash = infoHash.ToLowerInvariant();
        if (normalizedName == query || normalizedHash == query) return (4, 1);
        if (normalizedName.StartsWith(query, StringComparison.Ordinal) || normalizedHash.StartsWith(query, StringComparison.Ordinal)) return (3, 1);
        if (normalizedName.Contains(query, StringComparison.Ordinal) || normalizedHash.Contains(query, StringComparison.Ordinal)) return (2, 1);
        var similarity = query.Length >= 4
            ? normalizedName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Append(normalizedName).Select(part => TrigramSimilarity(query, part)).DefaultIfEmpty(0).Max()
            : 0;
        return similarity >= .45d ? (1, similarity) : (0, 0);
    }

    private static double TrigramSimilarity(string left, string right)
    {
        var a = Trigrams(left);
        var b = Trigrams(right);
        return a.Count == 0 || b.Count == 0 ? 0 : 2d * a.Intersect(b, StringComparer.Ordinal).Count() / (a.Count + b.Count);
    }

    private static HashSet<string> Trigrams(string value)
    {
        var padded = $"  {value} ";
        return Enumerable.Range(0, padded.Length - 2).Select(i => padded.Substring(i, 3)).ToHashSet(StringComparer.Ordinal);
    }

    public override async Task<TorrentInfo> GetTorrent(TorrentIdRequest request, ServerCallContext context)
    {
        var entity = await RequireOwned(request.Id);
        return TorrentMapper.ToInfo(entity, _engine.Get(entity.Id));
    }

    public override async Task<ListFilesResponse> ListFiles(TorrentIdRequest request, ServerCallContext context)
    {
        var entity = await RequireOwned(request.Id);
        var managed = _engine.Get(entity.Id);
        var response = new ListFilesResponse();

        if (managed != null && managed.Manager.HasMetadata)
        {
            for (var i = 0; i < managed.Manager.Files.Count; i++)
                response.Files.Add(TorrentMapper.ToFileInfo(managed.Manager.Files[i], i));
        }
        else
        {
            foreach (var f in entity.Files.OrderBy(f => f.Index))
                response.Files.Add(new TorrentFileInfo
                {
                    Index = f.Index,
                    Path = f.Path,
                    Size = f.Size,
                    Priority = (TorrentFilePriority)f.Priority,
                });
        }

        return response;
    }

    public override async Task<TorrentEmpty> PauseTorrent(TorrentIdRequest request, ServerCallContext context)
    {
        var entity = await RequireOwned(request.Id);
        entity.Paused = true;
        await _store.SaveChanges();
        await _engine.PauseAsync(entity.Id);
        return new TorrentEmpty();
    }

    public override async Task<TorrentEmpty> ResumeTorrent(TorrentIdRequest request, ServerCallContext context)
    {
        var entity = await RequireOwned(request.Id);
        entity.Paused = false;
        await _store.SaveChanges();
        await _engine.ResumeAsync(entity.Id);
        return new TorrentEmpty();
    }

    public override async Task<TorrentEmpty> RemoveTorrent(RemoveTorrentRequest request, ServerCallContext context)
    {
        var entity = await RequireOwned(request.Id);
        await _engine.RemoveAsync(entity.Id, request.DeleteFiles);
        await _store.Remove(entity);
        _metrics.Increment("torrents_removed");
        return new TorrentEmpty();
    }

    public override async Task<TorrentEmpty> SetFilePriority(SetFilePriorityRequest request, ServerCallContext context)
    {
        var entity = await RequireOwned(request.Id);
        var managed = _engine.Get(entity.Id);
        if (managed == null)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Торрент не активен"));

        await _engine.SetFilePriorityAsync(entity.Id, request.FileIndex, TorrentMapper.ToMonoTorrentPriority(request.Priority));

        // Персистим приоритет для восстановления после рестарта.
        var fileRow = entity.Files.FirstOrDefault(f => f.Index == request.FileIndex);
        if (fileRow == null && request.FileIndex >= 0 && request.FileIndex < managed.Manager.Files.Count)
        {
            var mf = managed.Manager.Files[request.FileIndex];
            fileRow = new TorrentFileEntity
            {
                Id = Guid.NewGuid(),
                TorrentId = entity.Id,
                Index = request.FileIndex,
                Path = mf.Path.ToString(),
                Size = mf.Length,
            };
            entity.Files.Add(fileRow);
        }

        if (fileRow != null)
            fileRow.Priority = (int)request.Priority;

        await _store.SaveChanges();
        return new TorrentEmpty();
    }

    public override async Task<ImportToCloudResponse> ImportToCloud(ImportToCloudRequest request, ServerCallContext context)
    {
        var entity = await RequireOwned(request.Id);
        var managed = _engine.Get(entity.Id);
        if (managed == null)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Торрент не активен"));

        var userToken = ExtractToken(context);
        if (string.IsNullOrEmpty(userToken))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Нет токена пользователя"));

        var files = managed.Manager.Files;
        var indices = request.HasFileIndex
            ? new[] { request.FileIndex }
            : Enumerable.Range(0, files.Count).ToArray();

        var response = new ImportToCloudResponse();
        foreach (var i in indices)
        {
            if (i < 0 || i >= files.Count)
                continue;

            // Импортируем только завершённые файлы.
            if (files[i].BytesDownloaded() < files[i].Length)
                continue;

            var imported = await _import.ImportFileAsync(files[i], request.DirectoryId, userToken, context.CancellationToken);
            if (imported != null)
                response.Files.Add(new ImportedFile { FileId = imported.FileId, Name = imported.Name });
        }

        _metrics.Increment("torrents_imported");
        return response;
    }

    public override async Task StreamProgress(
        StreamProgressRequest request,
        IServerStreamWriter<TorrentProgressSnapshot> responseStream,
        ServerCallContext context)
    {
        var userId = UserId;
        while (!context.CancellationToken.IsCancellationRequested)
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<ITorrentStore>();
                var entities = await store.ListByUser(userId);

                var snapshot = new TorrentProgressSnapshot();
                foreach (var e in entities)
                    snapshot.Torrents.Add(TorrentMapper.ToInfo(e, _engine.Get(e.Id)));

                await responseStream.WriteAsync(snapshot);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1500), context.CancellationToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task<TorrentEntity> RequireOwned(string id)
    {
        if (!Guid.TryParse(id, out var guid))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Некорректный id"));

        var entity = await _store.Get(guid, UserId);
        if (entity == null)
            throw new RpcException(new Status(StatusCode.NotFound, "Торрент не найден"));

        return entity;
    }

    private static string? ExtractToken(ServerCallContext context)
    {
        var headers = context.RequestHeaders;
        var xauth = headers.GetValue("x-auth-token");
        if (!string.IsNullOrEmpty(xauth))
            return xauth;

        var auth = headers.GetValue("authorization");
        if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth.Substring("Bearer ".Length);

        return auth;
    }
}
