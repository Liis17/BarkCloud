using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.MoveFileEntry;

public class MoveFileEntryCommand : IRequest<CloudEmpty>
{
    public Guid EntryId { get; set; }

    /// <summary>
    /// Новая директория. null означает перемещение в корень владельца.
    /// </summary>
    public Guid? NewDirectoryId { get; set; }
}
