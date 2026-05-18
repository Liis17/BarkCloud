using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ListDirectory;

public class ListDirectoryCommand : IRequest<DirectoryListing>
{
    /// <summary>
    /// Идентификатор директории. null означает корень владельца.
    /// </summary>
    public Guid? DirectoryId { get; set; }
}
