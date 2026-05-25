using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.EmptyTrash;

public class EmptyTrashCommand : IRequest<CloudEmpty>
{
}
