using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.CopyFileEntry;

public class CopyFileEntryCommand : IRequest<CloudEmpty>
{
    /// <summary>
    /// Идентификатор существующей записи (источник копии).
    /// </summary>
    public Guid SourceEntryId { get; set; }

    /// <summary>
    /// Целевая папка. null означает корень владельца.
    /// </summary>
    public Guid? TargetDirectoryId { get; set; }

    /// <summary>
    /// Имя записи в целевой папке. Пустая строка / null — взять имя источника.
    /// </summary>
    public string? NewName { get; set; }
}
