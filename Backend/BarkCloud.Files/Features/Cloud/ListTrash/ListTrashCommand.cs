using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ListTrash;

public class ListTrashCommand : IRequest<ListTrashResponse>
{
    public int Limit { get; set; }

    /// <summary>Курсор по дате удаления (exclusive). null означает «с самых свежеудалённых».</summary>
    public DateTime? CursorDeletedAt { get; set; }

    /// <summary>Tie-breaker по идентификатору записи. null если курсор не задан.</summary>
    public Guid? CursorEntryId { get; set; }
}
