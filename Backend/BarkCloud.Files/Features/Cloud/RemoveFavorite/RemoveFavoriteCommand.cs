using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.RemoveFavorite;

public class RemoveFavoriteCommand : IRequest<CloudEmpty>
{
    public Guid FileId { get; set; }
}
