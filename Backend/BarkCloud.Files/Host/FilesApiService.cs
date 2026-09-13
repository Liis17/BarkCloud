using BarkCloud.Files.Features.CheckFileHash;
using BarkCloud.Files.Features.CheckFileHashes;
using BarkCloud.Files.Features.GetTempDownloadUrl;
using BarkCloud.Files.Features.GetUploadUrl;
using BarkCloud.Files.Features.GetFileMetadata;
using BarkCloud.Files.Features.GetUserStorageInfo;
using BarkCloud.Files.Services;
using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;
using BarkCloud.Shared.Identity;

using Grpc.Core;

using MediatR;

using Microsoft.AspNetCore.Authorization;

using UploadFileType = BarkCloud.Files.Domain.UploadFileType;
using DomainUploadSessionStatus = BarkCloud.Files.Domain.UploadSessionStatus;

namespace BarkCloud.Files.Host;

[Authorize(Policy = nameof(TokenType.User))]
public class FilesApiService : FilesApi.FilesApiBase
{
    private readonly IMediator _mediator;
    private readonly MetricsCollector _metrics;
    private readonly UploadSessionCoordinator _uploadSessions;
    private readonly RequestContext _requestContext;
    private readonly UserContext _userContext;

    public FilesApiService(
        IMediator mediator,
        MetricsCollector metrics,
        UploadSessionCoordinator uploadSessions,
        RequestContext requestContext,
        UserContext userContext)
    {
        _mediator = mediator;
        _metrics = metrics;
        _uploadSessions = uploadSessions;
        _requestContext = requestContext;
        _userContext = userContext;
    }

    public override async Task<UploadSessionResponse> CreateUploadSession(
        CreateUploadSessionRequest request,
        ServerCallContext context)
    {
        try
        {
            var result = await _uploadSessions.CreateAsync(
                GetOwnerId(),
                _requestContext.DeviceName,
                new CreateUploadSessionInput(
                    request.IdempotencyKey,
                    request.FileName,
                    request.FileSize,
                    request.ContentType,
                    request.Sha256),
                context.CancellationToken);
            _metrics.Increment("upload_sessions_created_total");
            return Map(result);
        }
        catch (UploadQuotaExceededException)
        {
            _metrics.Increment("upload_quota_rejections_total");
            throw;
        }
    }

    public override async Task<UploadSessionResponse> GetUploadSession(
        UploadSessionIdRequest request,
        ServerCallContext context) =>
        Map(await _uploadSessions.GetAsync(GetOwnerId(), ParseSessionId(request.SessionId), context.CancellationToken));

    public override async Task<UploadSessionResponse> ResumeUploadSession(
        UploadSessionIdRequest request,
        ServerCallContext context)
    {
        var result = await _uploadSessions.ResumeAsync(
            GetOwnerId(), ParseSessionId(request.SessionId), context.CancellationToken);
        _metrics.Increment("upload_resumes_total");
        return Map(result);
    }

    public override async Task<UploadSessionResponse> CompleteUploadSession(
        UploadSessionIdRequest request,
        ServerCallContext context)
    {
        var result = await _uploadSessions.CompleteAsync(
            GetOwnerId(), ParseSessionId(request.SessionId), context.CancellationToken);
        _metrics.Increment("upload_sessions_completed_total");
        return Map(result);
    }

    public override async Task<UploadSessionResponse> CancelUploadSession(
        UploadSessionIdRequest request,
        ServerCallContext context)
    {
        var result = await _uploadSessions.CancelAsync(
            GetOwnerId(), ParseSessionId(request.SessionId), context.CancellationToken);
        _metrics.Increment("upload_sessions_cancelled_total");
        return Map(result);
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

    private long GetOwnerId() => _userContext.UserId;

    private static Guid ParseSessionId(string value) =>
        Guid.TryParse(value, out var sessionId)
            ? sessionId
            : throw new BarkCloud.Shared.Exceptions.Files.InvalidUploadSessionRequestException();

    private static UploadSessionResponse Map(UploadSessionResult result)
    {
        var response = new UploadSessionResponse
        {
            SessionId = result.SessionId.ToString(),
            FileId = result.FileId.ToString(),
            Status = result.Status switch
            {
                DomainUploadSessionStatus.Uploading => Proto.Files.UploadSessionStatus.Uploading,
                DomainUploadSessionStatus.Processing => Proto.Files.UploadSessionStatus.Processing,
                DomainUploadSessionStatus.Ready => Proto.Files.UploadSessionStatus.Ready,
                DomainUploadSessionStatus.Failed => Proto.Files.UploadSessionStatus.Failed,
                DomainUploadSessionStatus.Cancelled => Proto.Files.UploadSessionStatus.Cancelled,
                DomainUploadSessionStatus.Expired => Proto.Files.UploadSessionStatus.Expired,
                _ => Proto.Files.UploadSessionStatus.Unspecified
            },
            FileSize = result.FileSize,
            PartSize = result.PartSize,
            ExpiresAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(
                DateTime.SpecifyKind(result.ExpiresAt, DateTimeKind.Utc)),
            UploadToken = result.UploadToken ?? string.Empty,
            ErrorCode = result.ErrorCode ?? string.Empty,
            ErrorMessage = result.ErrorMessage ?? string.Empty
        };
        response.UploadedParts.AddRange(result.UploadedParts.Select(x => new UploadPartInfo
        {
            PartNumber = x.PartNumber,
            Size = x.Size,
            HasEtag = !string.IsNullOrWhiteSpace(x.Etag)
        }));
        return response;
    }
}
