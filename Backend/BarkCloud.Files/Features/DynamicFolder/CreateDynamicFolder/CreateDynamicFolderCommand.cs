using BarkCloud.Files.Domain;
using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.DynamicFolder.CreateDynamicFolder;

public class CreateDynamicFolderCommand : IRequest<DynamicFolderInfo>
{
    public string Name { get; set; } = "";

    public DynamicFolderCriteria Criteria { get; set; } = new();

    public string? IconKey { get; set; }

    public string? CoverColor { get; set; }
}
