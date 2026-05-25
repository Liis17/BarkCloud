using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.AddFavorite;

public class AddFavoriteCommand : IRequest<CloudEmpty>
{
    public Guid FileId { get; set; }
}
