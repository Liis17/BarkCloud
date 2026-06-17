using BarkCloud.Files.Services;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Identity;

using Grpc.Core;

using Microsoft.AspNetCore.Authorization;

namespace BarkCloud.Files.Host;

[Authorize(Policy = nameof(TokenType.User))]
public class MusicApiService : MusicApi.MusicApiBase
{
    private readonly MusicLibraryService _music;

    public MusicApiService(MusicLibraryService music)
    {
        _music = music;
    }

    public override Task<ListMusicTracksResponse> ListTracks(ListMusicTracksRequest request, ServerCallContext context)
    {
        DateTime? cursorAt = null;
        Guid? cursorId = null;
        if (request.CursorCreatedAt is not null && !string.IsNullOrWhiteSpace(request.CursorFileId))
        {
            cursorAt = request.CursorCreatedAt.ToDateTime();
            cursorId = Guid.Parse(request.CursorFileId);
        }

        return _music.ListTracks(request.Query, request.Limit, cursorAt, cursorId, context.CancellationToken);
    }

    public override Task<GetTrackDownloadUrlResponse> GetTrackDownloadUrl(GetTrackDownloadUrlRequest request, ServerCallContext context)
    {
        return _music.GetTrackDownloadUrl(Guid.Parse(request.FileId), context.CancellationToken);
    }

    public override Task<MusicPlaylistInfo> CreatePlaylist(CreateMusicPlaylistRequest request, ServerCallContext context)
    {
        return _music.CreatePlaylist(request.Name, request.Description, context.CancellationToken);
    }

    public override Task<MusicPlaylistInfo> UpdatePlaylist(UpdateMusicPlaylistRequest request, ServerCallContext context)
    {
        return _music.UpdatePlaylist(
            Guid.Parse(request.PlaylistId),
            request.HasName ? request.Name : null,
            request.HasDescription ? request.Description : null,
            request.HasCoverFileId,
            request.HasCoverFileId && !string.IsNullOrWhiteSpace(request.CoverFileId) ? Guid.Parse(request.CoverFileId) : null,
            context.CancellationToken);
    }

    public override Task<CloudEmpty> DeletePlaylist(DeleteMusicPlaylistRequest request, ServerCallContext context)
    {
        return _music.DeletePlaylist(Guid.Parse(request.PlaylistId), context.CancellationToken);
    }

    public override Task<ListMusicPlaylistsResponse> ListPlaylists(ListMusicPlaylistsRequest request, ServerCallContext context)
    {
        DateTime? cursorAt = null;
        Guid? cursorId = null;
        if (request.CursorUpdatedAt is not null && !string.IsNullOrWhiteSpace(request.CursorPlaylistId))
        {
            cursorAt = request.CursorUpdatedAt.ToDateTime();
            cursorId = Guid.Parse(request.CursorPlaylistId);
        }

        return _music.ListPlaylists(request.Limit, cursorAt, cursorId, context.CancellationToken);
    }

    public override Task<ListMusicPlaylistTracksResponse> ListPlaylistTracks(ListMusicPlaylistTracksRequest request, ServerCallContext context)
    {
        return _music.ListPlaylistTracks(Guid.Parse(request.PlaylistId), context.CancellationToken);
    }

    public override Task<CloudEmpty> AddPlaylistTracks(AddMusicPlaylistTracksRequest request, ServerCallContext context)
    {
        return _music.AddPlaylistTracks(Guid.Parse(request.PlaylistId), request.FileIds.Select(Guid.Parse).ToList(), context.CancellationToken);
    }

    public override Task<CloudEmpty> RemovePlaylistTracks(RemoveMusicPlaylistTracksRequest request, ServerCallContext context)
    {
        return _music.RemovePlaylistTracks(Guid.Parse(request.PlaylistId), request.FileIds.Select(Guid.Parse).ToList(), context.CancellationToken);
    }

    public override Task<CloudEmpty> ReorderPlaylistTracks(ReorderMusicPlaylistTracksRequest request, ServerCallContext context)
    {
        return _music.ReorderPlaylistTracks(Guid.Parse(request.PlaylistId), request.FileIds.Select(Guid.Parse).ToList(), context.CancellationToken);
    }

    public override Task<MusicPlaylistShareInfo> CreatePlaylistShare(CreateMusicPlaylistShareRequest request, ServerCallContext context)
    {
        return _music.CreatePlaylistShare(Guid.Parse(request.PlaylistId), request.Name, context.CancellationToken);
    }

    public override Task<ListMyMusicPlaylistSharesResponse> ListMyPlaylistShares(ListMyMusicPlaylistSharesRequest request, ServerCallContext context)
    {
        DateTime? cursorAt = null;
        Guid? cursorId = null;
        if (request.CursorCreatedAt is not null && !string.IsNullOrWhiteSpace(request.CursorShareId))
        {
            cursorAt = request.CursorCreatedAt.ToDateTime();
            cursorId = Guid.Parse(request.CursorShareId);
        }

        return _music.ListMyPlaylistShares(request.Limit, cursorAt, cursorId, context.CancellationToken);
    }

    public override Task<CloudEmpty> RevokePlaylistShare(RevokeMusicPlaylistShareRequest request, ServerCallContext context)
    {
        return _music.RevokePlaylistShare(Guid.Parse(request.ShareId), context.CancellationToken);
    }

    public override Task<CloudEmpty> SharePlaylistWithUser(ShareMusicPlaylistWithUserRequest request, ServerCallContext context)
    {
        return _music.SharePlaylistWithUser(Guid.Parse(request.PlaylistId), request.RecipientUserId, context.CancellationToken);
    }

    public override Task<CloudEmpty> RevokePlaylistUserShare(RevokeMusicPlaylistUserShareRequest request, ServerCallContext context)
    {
        return _music.RevokePlaylistUserShare(Guid.Parse(request.GrantId), context.CancellationToken);
    }

    public override Task<ListMyOutgoingMusicPlaylistSharesResponse> ListMyOutgoingPlaylistShares(ListMyOutgoingMusicPlaylistSharesRequest request, ServerCallContext context)
    {
        return _music.ListMyOutgoingPlaylistShares(Guid.Parse(request.PlaylistId), context.CancellationToken);
    }

    public override Task<ListSharedMusicPlaylistsWithMeResponse> ListSharedPlaylistsWithMe(ListSharedMusicPlaylistsWithMeRequest request, ServerCallContext context)
    {
        return _music.ListSharedPlaylistsWithMe(context.CancellationToken);
    }
}
