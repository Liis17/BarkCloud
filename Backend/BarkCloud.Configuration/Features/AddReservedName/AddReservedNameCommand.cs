using BarkCloud.Proto.Configuration;

using MediatR;

namespace BarkCloud.Configuration.Features.AddReservedName;

public class AddReservedNameCommand : IRequest<AddReservedNameResponse>
{
    public string Name { get; set; }
}
