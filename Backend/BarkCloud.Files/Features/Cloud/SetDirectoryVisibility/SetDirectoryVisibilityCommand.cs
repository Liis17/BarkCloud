using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.SetDirectoryVisibility;

public class SetDirectoryVisibilityCommand : IRequest<DirectoryProperties>
{
    public Guid DirectoryId { get; set; }
    public bool Inherit { get; set; }
    public bool HideFromGallery { get; set; }
    public bool HideFromSearch { get; set; }
}
