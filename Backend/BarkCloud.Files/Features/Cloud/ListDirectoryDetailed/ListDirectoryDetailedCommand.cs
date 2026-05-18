using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ListDirectoryDetailed;

public class ListDirectoryDetailedCommand : IRequest<DirectoryListingDetailed>
{
    /// <summary>
    /// Идентификатор директории. null означает корень владельца.
    /// </summary>
    public Guid? DirectoryId { get; set; }
}
