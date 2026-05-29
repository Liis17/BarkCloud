using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.CreateShare;

public class CreateShareCommand : IRequest<ShareInfo>
{
    public Guid FileId { get; set; }

    public string Name { get; set; } = string.Empty;
}
