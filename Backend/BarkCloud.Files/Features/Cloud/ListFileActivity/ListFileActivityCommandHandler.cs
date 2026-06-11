using BarkCloud.Files.Domain;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;
using BarkCloud.Shared.Exceptions.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ListFileActivity;

public class ListFileActivityCommandHandler : IRequestHandler<ListFileActivityCommand, ListFileActivityResponse>
{
    private readonly IFileActivityStorage _activityStorage;
    private readonly IUploadedFilesStorage _filesStorage;
    private readonly UserContext _userContext;

    public ListFileActivityCommandHandler(
        IFileActivityStorage activityStorage,
        IUploadedFilesStorage filesStorage,
        UserContext userContext)
    {
        _activityStorage = activityStorage;
        _filesStorage = filesStorage;
        _userContext = userContext;
    }

    public async Task<ListFileActivityResponse> Handle(ListFileActivityCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;

        var file = await _filesStorage.GetFile(request.FileId);
        if (file is null)
            throw new BarkCloud.Shared.Exceptions.Files.FileNotFoundException();
        if (!file.Uploaders.Contains(ownerId))
            throw new CloudAccessDeniedException();

        var limit = request.Limit is > 0 and <= 100 ? request.Limit : 30;
        var events = await _activityStorage.ListPage(
            ownerId,
            request.FileId,
            request.CursorCreatedAt,
            request.CursorEventId,
            limit,
            cancellationToken);

        var page = events.Take(limit).ToList();
        var response = new ListFileActivityResponse();
        response.Items.AddRange(page.Select(ToGrpc));

        if (events.Count > limit)
        {
            var last = page[^1];
            response.NextCursorCreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(last.CreatedAt, DateTimeKind.Utc));
            response.NextCursorEventId = last.Id.ToString();
        }

        return response;
    }

    private static FileActivityInfo ToGrpc(FileActivityEvent activity)
    {
        return new FileActivityInfo
        {
            Id = activity.Id.ToString(),
            FileId = activity.FileId.ToString(),
            EntryId = activity.EntryId?.ToString() ?? string.Empty,
            ActorUserId = activity.ActorUserId,
            Kind = activity.Kind,
            Summary = activity.Summary,
            DetailsJson = activity.DetailsJson,
            CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(activity.CreatedAt, DateTimeKind.Utc))
        };
    }
}
