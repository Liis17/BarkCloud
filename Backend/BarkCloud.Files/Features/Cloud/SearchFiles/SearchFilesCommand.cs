using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.SearchFiles;

public class SearchFilesCommand : IRequest<SearchFilesResponse>
{
    public string Query { get; set; } = string.Empty;

    public int Limit { get; set; }

    public DateTime? CursorCreatedAt { get; set; }

    public Guid? CursorEntryId { get; set; }
}
