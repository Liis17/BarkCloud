using BarkCloud.Proto.Configuration;

using MediatR;

namespace BarkCloud.Configuration.Features.DeleteReservedName;

public class DeleteReservedNameCommand : IRequest<DeleteReservedNameResponse>
{
    public string Name { get; set; }
}
