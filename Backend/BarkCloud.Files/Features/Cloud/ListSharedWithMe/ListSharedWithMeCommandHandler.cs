using BarkCloud.Files.Helpers;
using BarkCloud.Files.Mapping;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ListSharedWithMe;

public class ListSharedWithMeCommandHandler : IRequestHandler<ListSharedWithMeCommand, ListSharedWithMeResponse>
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private readonly IGrantStorage _grantStorage;
    private readonly IUploadedFilesStorage _filesStorage;
    private readonly UserContext _userContext;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;

    public ListSharedWithMeCommandHandler(
        IGrantStorage grantStorage,
        IUploadedFilesStorage filesStorage,
        UserContext userContext,
        RunSettings runSettings,
        IConfiguration configuration)
    {
        _grantStorage = grantStorage;
        _filesStorage = filesStorage;
        _userContext = userContext;
        _runSettings = runSettings;
        _configuration = configuration;
    }

    public async Task<ListSharedWithMeResponse> Handle(ListSharedWithMeCommand request, CancellationToken cancellationToken)
    {
        var recipientId = _userContext.UserId;
        var limit = request.Limit <= 0 ? DefaultLimit : Math.Min(request.Limit, MaxLimit);

        // Строго гранты этого получателя; берём limit+1 для определения следующей страницы.
        var grants = await _grantStorage.ListSharedWithMePage(
            recipientId, request.CursorSharedAt, request.CursorGrantId, limit, cancellationToken);

        var hasMore = grants.Count > limit;
        var page = hasMore ? grants.Take(limit).ToList() : grants;

        var baseUrl = FileUrlHelper.GetPublicBaseUrl(_configuration, _runSettings);
        var fileIds = page.Select(g => g.FileId).Distinct().ToList();
        var previewsByFile = await _filesStorage.GetPreviewsForFiles(fileIds, cancellationToken);

        var response = new ListSharedWithMeResponse();
        foreach (var g in page)
        {
            var file = await _filesStorage.GetFile(g.FileId);
            if (file is null)
                continue; // файл удалён владельцем — пропускаем (висящий грант подчистит TrashPurge)

            previewsByFile.TryGetValue(g.FileId, out var previews);
            response.Items.Add(new SharedWithMeEntry
            {
                GrantId = g.Id.ToString(),
                File = file.ToGrpc(baseUrl, previews),
                OwnerUserId = g.OwnerId,
                SharedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(g.CreatedAt, DateTimeKind.Utc))
            });
        }

        if (hasMore)
        {
            var last = page[^1];
            response.NextCursorSharedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(last.CreatedAt, DateTimeKind.Utc));
            response.NextCursorGrantId = last.Id.ToString();
        }

        return response;
    }
}
