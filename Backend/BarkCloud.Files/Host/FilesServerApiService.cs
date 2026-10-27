using BarkCloud.Files.Features.Cloud.ResolveShare;
using BarkCloud.Files.Features.Cloud.ResolveFolderShare;
using BarkCloud.Files.Features.Cloud.ResolveAlbumShare;
using BarkCloud.Files.Features.GetFileData;
using BarkCloud.Files.Features.UploadAvatarServer;
using BarkCloud.Files.Services;
using BarkCloud.Files.Infrastructure;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Identity;

using Grpc.Core;

using MediatR;

using Microsoft.AspNetCore.Authorization;

namespace BarkCloud.Files.Host;

/// <summary>
/// Межсервисное API: класс — минимум «валидный сервисный токен для Files», каждый метод
/// дополнительно требует своё право (см. <see cref="ServicePermissions"/>).
/// </summary>
[Authorize(Policy = nameof(TokenType.Service))]
public class FilesServerApiService : FilesServerApi.FilesServerApiBase
{
    private readonly IMediator _mediator;
    private readonly MusicLibraryService _music;
    private readonly StorageMigrationGate? _migrationGate;

    public FilesServerApiService(IMediator mediator, MusicLibraryService music, StorageMigrationGate? migrationGate = null)
    {
        _mediator = mediator;
        _music = music;
        _migrationGate = migrationGate;
    }

    private StorageMigrationGate MigrationGate => _migrationGate
        ?? throw new RpcException(new Status(StatusCode.Unimplemented, "Миграция S3 не настроена."));

    [Authorize(Policy = XAuthPolicies.FilesServerStorageAdminPolicy)]
    public override Task<StorageCutoverStatus> BeginStorageCutover(StorageCutoverRequest request, ServerCallContext context)
        => MigrationGate.BeginAsync(request, context.CancellationToken);
    [Authorize(Policy = XAuthPolicies.FilesServerStorageAdminPolicy)]
    public override Task<StorageCutoverStatus> FreezeStorageCutover(StorageCutoverIdRequest request, ServerCallContext context)
        => MigrationGate.FreezeAsync(request.MigrationId, context.CancellationToken);
    [Authorize(Policy = XAuthPolicies.FilesServerStorageAdminPolicy)]
    public override Task<StorageCutoverStatus> MarkStorageCutoverApplying(StorageCutoverIdRequest request, ServerCallContext context)
        => MigrationGate.MarkApplyingAsync(request.MigrationId, context.CancellationToken);
    [Authorize(Policy = XAuthPolicies.FilesServerStorageAdminPolicy)]
    public override Task<StorageCutoverStatus> CancelStorageCutover(StorageCutoverIdRequest request, ServerCallContext context)
        => MigrationGate.CancelAsync(request.MigrationId, context.CancellationToken);
    [Authorize(Policy = XAuthPolicies.FilesServerStorageAdminPolicy)]
    public override Task<StorageCutoversResponse> GetStorageCutovers(StorageCutoverIdRequest request, ServerCallContext context)
        => MigrationGate.GetAsync(request.MigrationId, context.CancellationToken);

    [Authorize(Policy = XAuthPolicies.FilesServerReadPolicy)]
    public override Task<GetFileDataResponse> GetFileData(GetFileDataRequest request, ServerCallContext context)
    {
        var command = new GetFileDataCommand()
        {
            FileId = Guid.Parse(request.FileId),
            UserId = request.UserId
        };

        return _mediator.Send(command);
    }

    [Authorize(Policy = XAuthPolicies.FilesServerWritePolicy)]
    public override Task<UploadAvatarServerResponse> UploadAvatarServer(UploadAvatarServerRequest request, ServerCallContext context)
    {
        var command = new UploadAvatarServerCommand
        {
            ImageData = request.ImageData.ToByteArray(),
            Filename = request.Filename,
            UserId = request.UserId
        };

        return _mediator.Send(command);
    }

    [Authorize(Policy = XAuthPolicies.FilesServerReadPolicy)]
    public override Task<ResolveShareResponse> ResolveShare(ResolveShareRequest request, ServerCallContext context)
    {
        return _mediator.Send(new ResolveShareCommand { Token = request.Token });
    }

    [Authorize(Policy = XAuthPolicies.FilesServerReadPolicy)]
    public override Task<ResolveFolderShareResponse> ResolveFolderShare(ResolveFolderShareRequest request, ServerCallContext context)
    {
        return _mediator.Send(new ResolveFolderShareCommand { Token = request.Token, Dir = request.Dir });
    }

    [Authorize(Policy = XAuthPolicies.FilesServerReadPolicy)]
    public override Task<ResolveAlbumShareResponse> ResolveAlbumShare(ResolveAlbumShareRequest request, ServerCallContext context)
    {
        DateTime? cursorAddedAt = null;
        Guid? cursorFileId = null;
        if (request.CursorAddedAt is not null && !string.IsNullOrWhiteSpace(request.CursorFileId))
        {
            cursorAddedAt = request.CursorAddedAt.ToDateTime();
            cursorFileId = Guid.Parse(request.CursorFileId);
        }

        return _mediator.Send(new ResolveAlbumShareCommand
        {
            Token = request.Token,
            Limit = request.Limit,
            CursorAddedAt = cursorAddedAt,
            CursorFileId = cursorFileId
        });
    }

    [Authorize(Policy = XAuthPolicies.FilesServerReadPolicy)]
    public override Task<ResolveMusicPlaylistShareResponse> ResolveMusicPlaylistShare(ResolveMusicPlaylistShareRequest request, ServerCallContext context)
    {
        return _music.ResolvePublicPlaylist(request.Token, context.CancellationToken);
    }
}
