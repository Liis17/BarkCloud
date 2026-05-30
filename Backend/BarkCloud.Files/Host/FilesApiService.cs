using BarkCloud.Files.Features.CheckFileHash;
using BarkCloud.Files.Features.CheckFileHashes;
using BarkCloud.Files.Features.GetTempDownloadUrl;
using BarkCloud.Files.Features.GetUploadUrl;
using BarkCloud.Files.Features.GetFileMetadata;
using BarkCloud.Files.Features.GetUserStorageInfo;
using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Identity;

using Grpc.Core;

using MediatR;

using Microsoft.AspNetCore.Authorization;

using UploadFileType = BarkCloud.Files.Domain.UploadFileType;

namespace BarkCloud.Files.Host;

[Authorize(Policy = nameof(TokenType.User))]
public class FilesApiService : FilesApi.FilesApiBase
{
    private readonly IMediator _mediator;
    private readonly MetricsCollector _metrics;

    public FilesApiService(IMediator mediator, MetricsCollector metrics)
    {
        _mediator = mediator;
        _metrics = metrics;
    }

    public override Task<GetUploadUrlResponse> GetUploadUrl(GetUploadUrlRequest request, ServerCallContext context)
    {
        var command = new GetUploadUrlCommand()
        {
            Type = (UploadFileType)(int)request.FileType
        };

        return _mediator.Send(command);
    }


    public override async Task<GetTempDownloadUrlResponse> GetTempDownloadUrl(GetTempDownloadUrlRequest request, ServerCallContext context)
    {
        _metrics.Increment("files_downloaded");
        var guids = request.FileIds.Select(Guid.Parse).ToList();

        var command = new GetTempDownloadUrlCommand()
        {
            FileIds = guids
        };

        return await _mediator.Send(command);
    }

    public override async Task<CheckFileHashResponse> CheckFileHash(CheckFileHashRequest request, ServerCallContext context)
    {
        var command = new CheckFileHashCommand()
        {
            FileHash = request.FileHash
        };

        return await _mediator.Send(command);
    }

    public override async Task<CheckFileHashesResponse> CheckFileHashes(CheckFileHashesRequest request, ServerCallContext context)
    {
        var command = new CheckFileHashesCommand()
        {
            FileHashes = request.FileHashes.ToList()
        };

        return await _mediator.Send(command);
    }

    public override async Task<GetUserStorageInfoResponse> GetUserStorageInfo(GetUserStorageInfoRequest request, ServerCallContext context)
    {
        var command = new GetUserStorageInfoCommand();

        return await _mediator.Send(command);
    }

    public override async Task<GetFileMetadataResponse> GetFileMetadata(GetFileMetadataRequest request, ServerCallContext context)
    {
        var command = new GetFileMetadataCommand
        {
            FileId = Guid.Parse(request.FileId)
        };

        return await _mediator.Send(command);
    }
}
