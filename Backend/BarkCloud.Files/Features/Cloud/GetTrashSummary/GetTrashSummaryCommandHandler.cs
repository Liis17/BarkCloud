using BarkCloud.Files.Persistence;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Files;

using Google.Protobuf.WellKnownTypes;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.GetTrashSummary;

/// <summary>
/// Сводка по корзине владельца: число записей и ближайшая дата авто-удаления
/// (минимальный PurgeAt). Используется бейджами и виджетами, чтобы показать «самый
/// истекающий» файл без выгрузки и сортировки всех страниц на клиенте.
/// </summary>
public class GetTrashSummaryCommandHandler : IRequestHandler<GetTrashSummaryCommand, GetTrashSummaryResponse>
{
    private readonly ICloudHierarchyStorage _storage;
    private readonly UserContext _userContext;

    public GetTrashSummaryCommandHandler(ICloudHierarchyStorage storage, UserContext userContext)
    {
        _storage = storage;
        _userContext = userContext;
    }

    public async Task<GetTrashSummaryResponse> Handle(GetTrashSummaryCommand request, CancellationToken cancellationToken)
    {
        var (count, oldestPurgeAt) = await _storage.GetTrashSummary(_userContext.UserId, cancellationToken);

        var response = new GetTrashSummaryResponse { TotalCount = count };
        if (oldestPurgeAt.HasValue)
            response.OldestPurgeAt = Timestamp.FromDateTime(DateTime.SpecifyKind(oldestPurgeAt.Value, DateTimeKind.Utc));

        return response;
    }
}
