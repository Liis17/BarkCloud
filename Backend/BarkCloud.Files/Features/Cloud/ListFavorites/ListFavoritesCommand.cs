using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ListFavorites;

public class ListFavoritesCommand : IRequest<ListFavoritesResponse>
{
    public int Limit { get; set; }

    /// <summary>Курсор по дате добавления в избранное (exclusive). null = с самых свежих.</summary>
    public DateTime? CursorCreatedAt { get; set; }

    /// <summary>Tie-breaker по идентификатору файла. null если курсор не задан.</summary>
    public Guid? CursorFileId { get; set; }
}
