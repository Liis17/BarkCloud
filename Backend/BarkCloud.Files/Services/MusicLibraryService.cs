using System.Security.Cryptography;

using BarkCloud.Files.Domain;
using BarkCloud.Files.Helpers;
using BarkCloud.Files.Mapping;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using Google.Protobuf.WellKnownTypes;

using Microsoft.EntityFrameworkCore;

using DomainMediaKind = BarkCloud.Files.Domain.MediaKind;
using DomainUploadFileType = BarkCloud.Files.Domain.UploadFileType;

namespace BarkCloud.Files.Services;

public class MusicLibraryService
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private readonly FilesContext _context;
    private readonly IUploadedFilesStorage _filesStorage;
    private readonly ITempFilesStorage _tempFiles;
    private readonly UserContext _userContext;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;

    public MusicLibraryService(
        FilesContext context,
        IUploadedFilesStorage filesStorage,
        ITempFilesStorage tempFiles,
        UserContext userContext,
        RunSettings runSettings,
        IConfiguration configuration)
    {
        _context = context;
        _filesStorage = filesStorage;
        _tempFiles = tempFiles;
        _userContext = userContext;
        _runSettings = runSettings;
        _configuration = configuration;
    }

    public async Task<ListMusicTracksResponse> ListTracks(
        string? queryText,
        int requestedLimit,
        DateTime? cursorCreatedAt,
        Guid? cursorFileId,
        CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var limit = ClampLimit(requestedLimit);
        var query = BaseAudioQuery(ownerId);

        if (!string.IsNullOrWhiteSpace(queryText))
        {
            var q = queryText.Trim().ToLower();
            query = query.Where(f =>
                (f.Filename != null && f.Filename.ToLower().Contains(q))
                || _context.FileMetadata.Any(m => m.FileId == f.Id
                    && ((m.AudioTitle != null && m.AudioTitle.ToLower().Contains(q))
                        || (m.AudioArtist != null && m.AudioArtist.ToLower().Contains(q))
                        || (m.AudioAlbum != null && m.AudioAlbum.ToLower().Contains(q)))));
        }

        if (cursorCreatedAt.HasValue && cursorFileId.HasValue)
        {
            var cursorAt = DateTime.SpecifyKind(cursorCreatedAt.Value, DateTimeKind.Utc);
            var cursorId = cursorFileId.Value;
            query = query.Where(f =>
                f.CreatedAt < cursorAt
                || (f.CreatedAt == cursorAt && f.Id.ToString().CompareTo(cursorId.ToString()) < 0));
        }

        var page = await query
            .OrderByDescending(f => f.CreatedAt)
            .ThenByDescending(f => f.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        var hasMore = page.Count > limit;
        if (hasMore)
            page.RemoveAt(page.Count - 1);

        var response = new ListMusicTracksResponse();
        foreach (var track in await BuildTracks(page, cancellationToken))
            response.Items.Add(track);

        if (hasMore && page.Count > 0)
        {
            var last = page[^1];
            response.NextCursorCreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(last.CreatedAt, DateTimeKind.Utc));
            response.NextCursorFileId = last.Id.ToString();
        }

        return response;
    }

    public async Task<GetTrackDownloadUrlResponse> GetTrackDownloadUrl(Guid fileId, CancellationToken cancellationToken)
    {
        var file = await _filesStorage.GetFile(fileId);
        if (file is null || file.MediaKind != DomainMediaKind.Audio || !file.Uploaders.Contains(_userContext.UserId))
            throw new CloudAccessDeniedException();

        var temp = await _tempFiles.CreateTempFile(fileId);
        return new GetTrackDownloadUrlResponse
        {
            DownloadUrl = FileUrlHelper.GenerateDownloadUrl(BaseUrl(), temp.Id)
        };
    }

    public async Task<MusicPlaylistInfo> CreatePlaylist(string name, string? description, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var now = DateTime.UtcNow;
        var playlist = new MusicPlaylist
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            Name = NormalizeName(name),
            Description = description?.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        };

        _context.MusicPlaylists.Add(playlist);
        await _context.SaveChangesAsync(cancellationToken);
        return await ToPlaylistInfo(playlist, cancellationToken);
    }

    public async Task<MusicPlaylistInfo> UpdatePlaylist(
        Guid playlistId,
        string? name,
        string? description,
        bool updateCover,
        Guid? coverFileId,
        CancellationToken cancellationToken)
    {
        var playlist = await GetOwnedPlaylist(playlistId, cancellationToken);

        if (name is not null)
            playlist.Name = NormalizeName(name);
        if (description is not null)
            playlist.Description = description.Trim();
        if (updateCover)
        {
            if (coverFileId.HasValue)
            {
                var cover = await _filesStorage.GetFile(coverFileId.Value);
                if (cover is null || cover.MediaKind != DomainMediaKind.Photo || !cover.Uploaders.Contains(_userContext.UserId))
                    throw new CloudAccessDeniedException();
            }

            playlist.CoverFileId = coverFileId;
        }

        playlist.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        return await ToPlaylistInfo(playlist, cancellationToken);
    }

    public async Task<CloudEmpty> DeletePlaylist(Guid playlistId, CancellationToken cancellationToken)
    {
        var playlist = await GetOwnedPlaylist(playlistId, cancellationToken);
        await _context.MusicPlaylistItems.Where(x => x.PlaylistId == playlistId).ExecuteDeleteAsync(cancellationToken);
        await _context.MusicPlaylistShareLinks.Where(x => x.OwnerId == playlist.OwnerId && x.PlaylistId == playlistId).ExecuteDeleteAsync(cancellationToken);
        await _context.MusicPlaylistGrants.Where(x => x.OwnerId == playlist.OwnerId && x.PlaylistId == playlistId).ExecuteDeleteAsync(cancellationToken);
        _context.MusicPlaylists.Remove(playlist);
        await _context.SaveChangesAsync(cancellationToken);
        return new CloudEmpty();
    }

    public async Task<ListMusicPlaylistsResponse> ListPlaylists(
        int requestedLimit,
        DateTime? cursorUpdatedAt,
        Guid? cursorPlaylistId,
        CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var limit = ClampLimit(requestedLimit);
        var query = _context.MusicPlaylists.AsNoTracking().Where(x => x.OwnerId == ownerId);

        if (cursorUpdatedAt.HasValue && cursorPlaylistId.HasValue)
        {
            var cursorAt = DateTime.SpecifyKind(cursorUpdatedAt.Value, DateTimeKind.Utc);
            var cursorId = cursorPlaylistId.Value;
            query = query.Where(x =>
                x.UpdatedAt < cursorAt
                || (x.UpdatedAt == cursorAt && x.Id.ToString().CompareTo(cursorId.ToString()) < 0));
        }

        var page = await query
            .OrderByDescending(x => x.UpdatedAt)
            .ThenByDescending(x => x.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        var hasMore = page.Count > limit;
        if (hasMore)
            page.RemoveAt(page.Count - 1);

        var response = new ListMusicPlaylistsResponse();
        foreach (var playlist in page)
            response.Items.Add(await ToPlaylistInfo(playlist, cancellationToken));

        if (hasMore && page.Count > 0)
        {
            var last = page[^1];
            response.NextCursorUpdatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(last.UpdatedAt, DateTimeKind.Utc));
            response.NextCursorPlaylistId = last.Id.ToString();
        }

        return response;
    }

    public async Task<ListMusicPlaylistTracksResponse> ListPlaylistTracks(Guid playlistId, CancellationToken cancellationToken)
    {
        var playlist = await GetAccessiblePlaylist(playlistId, cancellationToken);
        var items = await _context.MusicPlaylistItems
            .AsNoTracking()
            .Where(x => x.PlaylistId == playlist.Id)
            .OrderBy(x => x.Position)
            .ThenBy(x => x.AddedAt)
            .ToListAsync(cancellationToken);

        var files = await _filesStorage.GetFiles(items.Select(x => x.FileId).Distinct().ToList());
        var tracks = (await BuildTracks(files, cancellationToken)).ToDictionary(x => Guid.Parse(x.File.Id));

        var response = new ListMusicPlaylistTracksResponse
        {
            Playlist = await ToPlaylistInfo(playlist, cancellationToken)
        };

        foreach (var item in items)
        {
            if (!tracks.TryGetValue(item.FileId, out var track))
                continue;

            response.Items.Add(new MusicPlaylistTrackEntry
            {
                Track = track,
                Position = item.Position,
                AddedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(item.AddedAt, DateTimeKind.Utc))
            });
        }

        return response;
    }

    public async Task<CloudEmpty> AddPlaylistTracks(Guid playlistId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken)
    {
        var playlist = await GetOwnedPlaylist(playlistId, cancellationToken);
        if (fileIds.Count == 0)
            return new CloudEmpty();

        var requested = fileIds.Distinct().ToList();
        var files = await _filesStorage.GetFiles(requested);
        if (files.Count != requested.Count || files.Any(f => f.MediaKind != DomainMediaKind.Audio || !f.Uploaders.Contains(_userContext.UserId)))
            throw new CloudAccessDeniedException();

        var existing = await _context.MusicPlaylistItems
            .AsNoTracking()
            .Where(x => x.PlaylistId == playlistId && requested.Contains(x.FileId))
            .Select(x => x.FileId)
            .ToListAsync(cancellationToken);

        var toAdd = requested.Where(x => !existing.Contains(x)).ToList();
        if (toAdd.Count == 0)
            return new CloudEmpty();

        var maxPosition = await _context.MusicPlaylistItems
            .Where(x => x.PlaylistId == playlistId)
            .Select(x => (int?)x.Position)
            .MaxAsync(cancellationToken) ?? 0;

        var now = DateTime.UtcNow;
        var position = maxPosition;
        _context.MusicPlaylistItems.AddRange(toAdd.Select(fileId => new MusicPlaylistItem
        {
            Id = Guid.NewGuid(),
            PlaylistId = playlistId,
            FileId = fileId,
            OwnerId = playlist.OwnerId,
            Position = ++position,
            AddedAt = now
        }));

        playlist.UpdatedAt = now;
        await _context.SaveChangesAsync(cancellationToken);
        return new CloudEmpty();
    }

    public async Task<CloudEmpty> RemovePlaylistTracks(Guid playlistId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken)
    {
        var playlist = await GetOwnedPlaylist(playlistId, cancellationToken);
        if (fileIds.Count == 0)
            return new CloudEmpty();

        await _context.MusicPlaylistItems
            .Where(x => x.PlaylistId == playlistId && fileIds.Contains(x.FileId))
            .ExecuteDeleteAsync(cancellationToken);

        playlist.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        await CompactPositions(playlistId, cancellationToken);
        return new CloudEmpty();
    }

    public async Task<CloudEmpty> ReorderPlaylistTracks(Guid playlistId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken)
    {
        var playlist = await GetOwnedPlaylist(playlistId, cancellationToken);
        var items = await _context.MusicPlaylistItems
            .Where(x => x.PlaylistId == playlistId)
            .ToListAsync(cancellationToken);

        var order = fileIds.Distinct().ToList();
        if (order.Count != items.Count || items.Any(x => !order.Contains(x.FileId)))
            throw new CloudAccessDeniedException();

        for (var i = 0; i < order.Count; i++)
        {
            var item = items.First(x => x.FileId == order[i]);
            item.Position = i + 1;
        }

        playlist.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        return new CloudEmpty();
    }

    public async Task<MusicPlaylistShareInfo> CreatePlaylistShare(Guid playlistId, string? name, CancellationToken cancellationToken)
    {
        var playlist = await GetOwnedPlaylist(playlistId, cancellationToken);
        var existing = await _context.MusicPlaylistShareLinks
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.OwnerId == playlist.OwnerId && x.PlaylistId == playlistId, cancellationToken);
        if (existing is not null)
            return await ToShareInfo(existing, cancellationToken);

        var share = new MusicPlaylistShareLink
        {
            Id = Guid.NewGuid(),
            OwnerId = playlist.OwnerId,
            PlaylistId = playlist.Id,
            Token = GenerateToken(),
            Name = string.IsNullOrWhiteSpace(name) ? playlist.Name : name.Trim(),
            CreatedAt = DateTime.UtcNow
        };

        _context.MusicPlaylistShareLinks.Add(share);
        await _context.SaveChangesAsync(cancellationToken);
        return await ToShareInfo(share, cancellationToken);
    }

    public async Task<ListMyMusicPlaylistSharesResponse> ListMyPlaylistShares(
        int requestedLimit,
        DateTime? cursorCreatedAt,
        Guid? cursorShareId,
        CancellationToken cancellationToken)
    {
        var limit = ClampLimit(requestedLimit);
        var ownerId = _userContext.UserId;
        var query = _context.MusicPlaylistShareLinks.AsNoTracking().Where(x => x.OwnerId == ownerId);

        if (cursorCreatedAt.HasValue && cursorShareId.HasValue)
        {
            var cursorAt = DateTime.SpecifyKind(cursorCreatedAt.Value, DateTimeKind.Utc);
            var cursorId = cursorShareId.Value;
            query = query.Where(x =>
                x.CreatedAt < cursorAt
                || (x.CreatedAt == cursorAt && x.Id.ToString().CompareTo(cursorId.ToString()) < 0));
        }

        var page = await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        var hasMore = page.Count > limit;
        if (hasMore)
            page.RemoveAt(page.Count - 1);

        var response = new ListMyMusicPlaylistSharesResponse();
        foreach (var share in page)
            response.Shares.Add(await ToShareInfo(share, cancellationToken));

        if (hasMore && page.Count > 0)
        {
            var last = page[^1];
            response.NextCursorCreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(last.CreatedAt, DateTimeKind.Utc));
            response.NextCursorShareId = last.Id.ToString();
        }

        return response;
    }

    public async Task<CloudEmpty> RevokePlaylistShare(Guid shareId, CancellationToken cancellationToken)
    {
        await _context.MusicPlaylistShareLinks
            .Where(x => x.OwnerId == _userContext.UserId && x.Id == shareId)
            .ExecuteDeleteAsync(cancellationToken);
        return new CloudEmpty();
    }

    public async Task<CloudEmpty> SharePlaylistWithUser(Guid playlistId, long recipientId, CancellationToken cancellationToken)
    {
        var playlist = await GetOwnedPlaylist(playlistId, cancellationToken);
        var exists = await _context.MusicPlaylistGrants
            .AnyAsync(x => x.OwnerId == playlist.OwnerId && x.PlaylistId == playlistId && x.RecipientId == recipientId, cancellationToken);
        if (!exists)
        {
            _context.MusicPlaylistGrants.Add(new MusicPlaylistGrant
            {
                Id = Guid.NewGuid(),
                OwnerId = playlist.OwnerId,
                PlaylistId = playlistId,
                RecipientId = recipientId,
                CreatedAt = DateTime.UtcNow
            });
            await _context.SaveChangesAsync(cancellationToken);
        }

        return new CloudEmpty();
    }

    public async Task<CloudEmpty> RevokePlaylistUserShare(Guid grantId, CancellationToken cancellationToken)
    {
        await _context.MusicPlaylistGrants
            .Where(x => x.OwnerId == _userContext.UserId && x.Id == grantId)
            .ExecuteDeleteAsync(cancellationToken);
        return new CloudEmpty();
    }

    public async Task<ListMyOutgoingMusicPlaylistSharesResponse> ListMyOutgoingPlaylistShares(Guid playlistId, CancellationToken cancellationToken)
    {
        var playlist = await GetOwnedPlaylist(playlistId, cancellationToken);
        var grants = await _context.MusicPlaylistGrants
            .AsNoTracking()
            .Where(x => x.OwnerId == playlist.OwnerId && x.PlaylistId == playlistId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        var response = new ListMyOutgoingMusicPlaylistSharesResponse();
        foreach (var grant in grants)
        {
            response.Items.Add(new OutgoingMusicPlaylistShare
            {
                GrantId = grant.Id.ToString(),
                PlaylistId = grant.PlaylistId.ToString(),
                Name = playlist.Name,
                RecipientUserId = grant.RecipientId,
                SharedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(grant.CreatedAt, DateTimeKind.Utc))
            });
        }

        return response;
    }

    public async Task<ListSharedMusicPlaylistsWithMeResponse> ListSharedPlaylistsWithMe(CancellationToken cancellationToken)
    {
        var recipientId = _userContext.UserId;
        var grants = await _context.MusicPlaylistGrants
            .AsNoTracking()
            .Where(x => x.RecipientId == recipientId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        var playlistIds = grants.Select(x => x.PlaylistId).Distinct().ToList();
        var playlists = await _context.MusicPlaylists
            .AsNoTracking()
            .Where(x => playlistIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        var response = new ListSharedMusicPlaylistsWithMeResponse();
        foreach (var grant in grants)
        {
            if (!playlists.TryGetValue(grant.PlaylistId, out var playlist))
                continue;

            response.Items.Add(new SharedMusicPlaylistEntry
            {
                GrantId = grant.Id.ToString(),
                Playlist = await ToPlaylistInfo(playlist, cancellationToken),
                OwnerUserId = grant.OwnerId,
                SharedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(grant.CreatedAt, DateTimeKind.Utc))
            });
        }

        return response;
    }

    public async Task<ResolveMusicPlaylistShareResponse> ResolvePublicPlaylist(string token, CancellationToken cancellationToken)
    {
        var share = await _context.MusicPlaylistShareLinks
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Token == token, cancellationToken);
        if (share is null)
            return new ResolveMusicPlaylistShareResponse { Found = false };

        var playlist = await _context.MusicPlaylists
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == share.PlaylistId, cancellationToken);
        if (playlist is null)
            return new ResolveMusicPlaylistShareResponse { Found = false };

        await _context.MusicPlaylistShareLinks
            .Where(x => x.Id == share.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ClickCount, x => x.ClickCount + 1), cancellationToken);

        var items = await _context.MusicPlaylistItems
            .AsNoTracking()
            .Where(x => x.PlaylistId == playlist.Id)
            .OrderBy(x => x.Position)
            .ToListAsync(cancellationToken);
        var files = await _filesStorage.GetFiles(items.Select(x => x.FileId).Distinct().ToList());
        var tracks = await BuildTracks(files, cancellationToken);

        var response = new ResolveMusicPlaylistShareResponse
        {
            Found = true,
            PlaylistName = playlist.Name,
            Description = playlist.Description ?? string.Empty,
            CoverPreviewUrl = await ResolvePlaylistCoverUrl(playlist, cancellationToken)
        };
        response.Items.AddRange(tracks);
        return response;
    }

    private IQueryable<UploadFile> BaseAudioQuery(long ownerId)
    {
        return _context.UploadedFiles
            .AsNoTracking()
            .Where(f => f.Uploaders.Contains(ownerId)
                        && f.Type == DomainUploadFileType.CloudFile
                        && f.MediaKind == DomainMediaKind.Audio
                        && !_context.FilePreviews.Any(p => p.PreviewFileId == f.Id)
                        && !(_context.CloudFileEntries.Any(e => e.OwnerId == ownerId && e.FileId == f.Id && e.IsDeleted)
                             && !_context.CloudFileEntries.Any(e => e.OwnerId == ownerId && e.FileId == f.Id && !e.IsDeleted)));
    }

    private async Task<List<MusicTrackInfo>> BuildTracks(IReadOnlyCollection<UploadFile> files, CancellationToken cancellationToken)
    {
        if (files.Count == 0)
            return new List<MusicTrackInfo>();

        var baseUrl = BaseUrl();
        var ids = files.Select(x => x.Id).ToList();
        var metadata = await _context.FileMetadata
            .AsNoTracking()
            .Where(x => ids.Contains(x.FileId))
            .ToDictionaryAsync(x => x.FileId, cancellationToken);
        var previews = await _filesStorage.GetPreviewsForFiles(ids, cancellationToken);
        var tempFiles = await _tempFiles.CreateTempFilesBatchAsync(ids, cancellationToken);
        var tempByFile = tempFiles
            .GroupBy(x => x.OriginalFileId)
            .ToDictionary(x => x.Key, x => x.First().Id);

        return files
            .Where(f => f.MediaKind == DomainMediaKind.Audio)
            .Select(file =>
            {
                metadata.TryGetValue(file.Id, out var meta);
                previews.TryGetValue(file.Id, out var filePreviews);

                var track = new MusicTrackInfo
                {
                    File = file.ToGrpc(baseUrl, filePreviews),
                    Title = string.IsNullOrWhiteSpace(meta?.AudioTitle)
                        ? Path.GetFileNameWithoutExtension(file.Filename ?? string.Empty)
                        : meta!.AudioTitle!,
                    Artist = meta?.AudioArtist ?? string.Empty,
                    Album = meta?.AudioAlbum ?? string.Empty,
                    DurationSeconds = meta?.DurationSeconds ?? 0,
                    CoverUrl = PickPreviewUrl(filePreviews, 128),
                    LargeCoverUrl = PickPreviewUrl(filePreviews, 512)
                };
                if (meta is not null)
                    track.Metadata = meta.ToGrpc();

                if (tempByFile.TryGetValue(file.Id, out var tempId))
                    track.File.FileUrl = FileUrlHelper.GenerateDownloadUrl(baseUrl, tempId);

                return track;
            })
            .ToList();
    }

    private async Task<MusicPlaylistInfo> ToPlaylistInfo(MusicPlaylist playlist, CancellationToken cancellationToken)
    {
        var count = await _context.MusicPlaylistItems.CountAsync(x => x.PlaylistId == playlist.Id, cancellationToken);
        return new MusicPlaylistInfo
        {
            Id = playlist.Id.ToString(),
            Name = playlist.Name,
            Description = playlist.Description ?? string.Empty,
            CoverFileId = playlist.CoverFileId?.ToString() ?? string.Empty,
            CoverPreviewUrl = await ResolvePlaylistCoverUrl(playlist, cancellationToken),
            ItemsCount = count,
            OwnerUserId = playlist.OwnerId,
            CanReorder = playlist.OwnerId == _userContext.UserId,
            CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(playlist.CreatedAt, DateTimeKind.Utc)),
            UpdatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(playlist.UpdatedAt, DateTimeKind.Utc))
        };
    }

    private async Task<string> ResolvePlaylistCoverUrl(MusicPlaylist playlist, CancellationToken cancellationToken)
    {
        if (playlist.CoverFileId.HasValue)
        {
            var previews = await _filesStorage.GetPreviewsForFile(playlist.CoverFileId.Value, cancellationToken);
            var custom = PickPreviewUrl(previews, 512);
            if (!string.IsNullOrEmpty(custom))
                return custom;
        }

        var first = await _context.MusicPlaylistItems
            .AsNoTracking()
            .Where(x => x.PlaylistId == playlist.Id)
            .OrderBy(x => x.Position)
            .FirstOrDefaultAsync(cancellationToken);
        if (first is null)
            return string.Empty;

        var trackPreviews = await _filesStorage.GetPreviewsForFile(first.FileId, cancellationToken);
        return PickPreviewUrl(trackPreviews, 512);
    }

    private async Task<MusicPlaylistShareInfo> ToShareInfo(MusicPlaylistShareLink share, CancellationToken cancellationToken)
    {
        var playlist = await _context.MusicPlaylists
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == share.PlaylistId, cancellationToken);

        return new MusicPlaylistShareInfo
        {
            Id = share.Id.ToString(),
            Token = share.Token,
            PlaylistId = share.PlaylistId.ToString(),
            Name = share.Name,
            CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(share.CreatedAt, DateTimeKind.Utc)),
            ClickCount = share.ClickCount,
            CoverPreviewUrl = playlist is null ? string.Empty : await ResolvePlaylistCoverUrl(playlist, cancellationToken)
        };
    }

    private async Task<MusicPlaylist> GetOwnedPlaylist(Guid playlistId, CancellationToken cancellationToken)
    {
        var playlist = await _context.MusicPlaylists
            .FirstOrDefaultAsync(x => x.Id == playlistId, cancellationToken);
        if (playlist is null || playlist.OwnerId != _userContext.UserId)
            throw new CloudAccessDeniedException();
        return playlist;
    }

    private async Task<MusicPlaylist> GetAccessiblePlaylist(Guid playlistId, CancellationToken cancellationToken)
    {
        var userId = _userContext.UserId;
        var playlist = await _context.MusicPlaylists
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == playlistId, cancellationToken);
        if (playlist is null)
            throw new CloudAccessDeniedException();
        if (playlist.OwnerId == userId)
            return playlist;

        var granted = await _context.MusicPlaylistGrants
            .AnyAsync(x => x.PlaylistId == playlistId && x.RecipientId == userId, cancellationToken);
        if (!granted)
            throw new CloudAccessDeniedException();

        return playlist;
    }

    private async Task CompactPositions(Guid playlistId, CancellationToken cancellationToken)
    {
        var items = await _context.MusicPlaylistItems
            .Where(x => x.PlaylistId == playlistId)
            .OrderBy(x => x.Position)
            .ToListAsync(cancellationToken);
        for (var i = 0; i < items.Count; i++)
            items[i].Position = i + 1;
        await _context.SaveChangesAsync(cancellationToken);
    }

    private string BaseUrl() => FileUrlHelper.GetPublicBaseUrl(_configuration, _runSettings);

    private static int ClampLimit(int requested) => requested <= 0 ? DefaultLimit : Math.Min(requested, MaxLimit);

    private static string NormalizeName(string name)
    {
        var value = name.Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Playlist name is empty", nameof(name));
        return value;
    }

    private string PickPreviewUrl(IReadOnlyCollection<FilePreview>? previews, int target)
    {
        if (previews is null || previews.Count == 0)
            return string.Empty;

        var preview = previews
            .Where(x => x.TargetWidth > 0)
            .OrderBy(x => Math.Abs(x.TargetWidth - target))
            .ThenByDescending(x => x.TargetWidth)
            .FirstOrDefault();

        return preview is null ? string.Empty : FileUrlHelper.GenerateDownloadUrl(BaseUrl(), preview.PreviewFileId);
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
