using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ListDirectoryDetailed;

public class ListDirectoryDetailedCommand : IRequest<DirectoryListingDetailed>
{
    /// <summary>
    /// Идентификатор директории. null означает корень владельца.
    /// </summary>
    public Guid? DirectoryId { get; set; }

    /// <summary>
    /// Размер страницы. Ноль означает значение по умолчанию.
    /// </summary>
    public int Limit { get; set; }

    /// <summary>
    /// Имя последнего элемента предыдущей страницы.
    /// </summary>
    public string? CursorName { get; set; }

    /// <summary>
    /// Идентификатор записи последнего элемента предыдущей страницы.
    /// </summary>
    public Guid? CursorEntryId { get; set; }
}
