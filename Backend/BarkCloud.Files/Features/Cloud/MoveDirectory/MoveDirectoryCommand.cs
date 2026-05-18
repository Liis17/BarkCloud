using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.MoveDirectory;

public class MoveDirectoryCommand : IRequest<CloudEmpty>
{
    public Guid DirectoryId { get; set; }

    /// <summary>
    /// Новый родитель. null означает перемещение в корень владельца.
    /// </summary>
    public Guid? NewParentId { get; set; }
}
