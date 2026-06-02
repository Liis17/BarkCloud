using BarkCloud.Files.Helpers;
using BarkCloud.Files.Mapping;
using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.Settings;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ListMyOutgoingSharesAll;

/// <summary>
/// Все исходящие гранты владельца («я поделился»): файлы, которыми пользователь поделился с другими,
/// и кому. Плоский список грантов (от новых к старым, cursor-пагинация). Группировку по файлу
/// и резолв получателей делает веб-слой.
/// </summary>
public class ListMyOutgoingSharesAllCommandHandler
    : IRequestHandler<ListMyOutgoingSharesAllCommand, ListMyOutgoingSharesAllResponse>
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private readonly IGrantStorage _grantStorage;
    private readonly IUploadedFilesStorage _filesStorage;
    private readonly UserContext _userContext;
    private readonly RunSettings _runSettings;
    private readonly IConfiguration _configuration;

    public ListMyOutgoingSharesAllCommandHandler(
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

    public async Task<ListMyOutgoingSharesAllResponse> Handle(
        ListMyOutgoingSharesAllCommand request, CancellationToken cancellationToken)
    {
        var ownerId = _userContext.UserId;
        var limit = request.Limit <= 0 ? DefaultLimit : Math.Min(request.Limit, MaxLimit);

        var grants = await _grantStorage.ListByOwnerPage(
            ownerId, request.CursorSharedAt, request.CursorGrantId, limit, cancellationToken);

        var hasMore = grants.Count > limit;
        var page = hasMore ? grants.Take(limit).ToList() : grants;

        var baseUrl = FileUrlHelper.GetPublicBaseUrl(_configuration, _runSettings);
        var fileIds = page.Select(g => g.FileId).Distinct().ToList();
        var previewsByFile = await _filesStorage.GetPreviewsForFiles(fileIds, cancellationToken);

        var response = new ListMyOutgoingSharesAllResponse();
        foreach (var g in page)
        {
            var file = await _filesStorage.GetFile(g.FileId);
            if (file is null)
                continue; // файл удалён — пропускаем (висящий грант подчистит TrashPurge)

            previewsByFile.TryGetValue(g.FileId, out var previews);
            response.Items.Add(new OutgoingShareFull
            {
                GrantId = g.Id.ToString(),
                File = file.ToGrpc(baseUrl, previews),
                RecipientUserId = g.RecipientId,
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
