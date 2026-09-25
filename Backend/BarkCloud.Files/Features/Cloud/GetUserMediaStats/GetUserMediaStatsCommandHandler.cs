using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using MediatR;

using DomainMediaKind = BarkCloud.Files.Domain.MediaKind;

namespace BarkCloud.Files.Features.Cloud.GetUserMediaStats;

public sealed class GetUserMediaStatsCommandHandler : IRequestHandler<GetUserMediaStatsCommand, GetUserMediaStatsResponse>
{
    private readonly IUploadedFilesStorage _uploadedFiles;
    private readonly UserContext _userContext;

    public GetUserMediaStatsCommandHandler(IUploadedFilesStorage uploadedFiles, UserContext userContext)
    {
        _uploadedFiles = uploadedFiles;
        _userContext = userContext;
    }

    public async Task<GetUserMediaStatsResponse> Handle(GetUserMediaStatsCommand request, CancellationToken cancellationToken)
    {
        var stats = await _uploadedFiles.GetUserMediaStats(
            _userContext.UserId, (DomainMediaKind)(int)request.Kind, cancellationToken);

        return new GetUserMediaStatsResponse
        {
            TotalCount = stats.TotalCount,
            TotalSizeBytes = stats.TotalSizeBytes
        };
    }
}
