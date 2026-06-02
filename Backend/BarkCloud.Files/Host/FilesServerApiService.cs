using BarkCloud.Files.Features.Cloud.ResolveShare;
using BarkCloud.Files.Features.Cloud.ResolveFolderShare;
using BarkCloud.Files.Features.GetFileData;
using BarkCloud.Files.Features.GetFilesData;
using BarkCloud.Files.Features.GetUserStorageInfoServer;
using BarkCloud.Files.Features.UploadAvatarServer;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Identity;

using Grpc.Core;

using MediatR;

using Microsoft.AspNetCore.Authorization;

namespace BarkCloud.Files.Host;

[Authorize(Policy = nameof(TokenType.Service))]
public class FilesServerApiService : FilesServerApi.FilesServerApiBase
{
    private readonly IMediator _mediator;

    public FilesServerApiService(IMediator mediator)
    {
        _mediator = mediator;
    }

    public override Task<GetFileDataResponse> GetFileData(GetFileDataRequest request, ServerCallContext context)
    {
        var command = new GetFileDataCommand()
        {
            FileId = Guid.Parse(request.FileId)
        };

        return _mediator.Send(command);
    }

    public override Task<GetFilesDataResponse> GetFilesData(GetFilesDataRequest request, ServerCallContext context)
    {
        var command = new GetFilesDataCommand()
        {
            FileIds = request.FileIds.Select(Guid.Parse).ToList()
        };

        return _mediator.Send(command);
    }

    public override Task<GetUserStorageInfoResponse> GetUserStorageInfoServer(GetUserStorageInfoServerRequest request, ServerCallContext context)
    {
        var command = new GetUserStorageInfoServerCommand
        {
            UserId = request.UserId
        };

        return _mediator.Send(command);
    }

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

    public override Task<ResolveShareResponse> ResolveShare(ResolveShareRequest request, ServerCallContext context)
    {
        return _mediator.Send(new ResolveShareCommand { Token = request.Token });
    }

    public override Task<ResolveFolderShareResponse> ResolveFolderShare(ResolveFolderShareRequest request, ServerCallContext context)
    {
        return _mediator.Send(new ResolveFolderShareCommand { Token = request.Token, Dir = request.Dir });
    }
}
