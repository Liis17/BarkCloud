using BarkCloud.Files.Domain;
using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.DynamicFolder.UpdateDynamicFolder;

public class UpdateDynamicFolderCommand : IRequest<DynamicFolderInfo>
{
    public Guid FolderId { get; set; }

    /// <summary>null = не менять имя.</summary>
    public string? Name { get; set; }

    /// <summary>Критерии заменяются целиком (комбинатор + правила).</summary>
    public DynamicFolderCriteria Criteria { get; set; } = new();

    /// <summary>null = не менять.</summary>
    public string? IconKey { get; set; }

    /// <summary>null = не менять.</summary>
    public string? CoverColor { get; set; }

    /// <summary>null = не менять режим отображения.</summary>
    public Domain.DfViewMode? ViewMode { get; set; }
}
