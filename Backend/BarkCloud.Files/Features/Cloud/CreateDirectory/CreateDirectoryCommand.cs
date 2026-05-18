using MediatR;

using DirectoryInfo = BarkCloud.Proto.Files.DirectoryInfo;

namespace BarkCloud.Files.Features.Cloud.CreateDirectory;

public class CreateDirectoryCommand : IRequest<DirectoryInfo>
{
    /// <summary>
    /// Идентификатор родительской папки. null означает корень владельца.
    /// </summary>
    public Guid? ParentId { get; set; }

    public string Name { get; set; } = "";
}
