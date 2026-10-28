using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.GetSubdirectorySizes;

public class GetSubdirectorySizesCommand : IRequest<GetSubdirectorySizesResponse>
{
    /// <summary>
    /// Родительская директория. null означает корень владельца.
    /// </summary>
    public Guid? ParentId { get; set; }
}
