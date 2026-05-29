using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ListMyShares;

public class ListMySharesCommand : IRequest<ListMySharesResponse>
{
    public int Limit { get; set; }

    /// <summary>Курсор по дате создания ссылки (exclusive). null = с самых свежих.</summary>
    public DateTime? CursorCreatedAt { get; set; }

    /// <summary>Tie-breaker по идентификатору ссылки. null если курсор не задан.</summary>
    public Guid? CursorShareId { get; set; }
}
