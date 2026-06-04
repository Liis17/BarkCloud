using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ListMediaLocations;

public class ListMediaLocationsCommand : IRequest<ListMediaLocationsResponse>
{
    public int Limit { get; set; }

    /// <summary>Курсор по дате создания (exclusive). null означает «с самых новых».</summary>
    public DateTime? CursorCreatedAt { get; set; }

    /// <summary>Tie-breaker для записей с одинаковым CreatedAt. null если курсор не задан.</summary>
    public Guid? CursorFileId { get; set; }
}
