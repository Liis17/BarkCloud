using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.DeleteUserMedia;

public class DeleteUserMediaCommand : IRequest<CloudEmpty>
{
    public Guid FileId { get; set; }
}
