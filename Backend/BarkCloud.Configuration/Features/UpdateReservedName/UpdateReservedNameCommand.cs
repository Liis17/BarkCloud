using BarkCloud.Proto.Configuration;

using MediatR;

namespace BarkCloud.Configuration.Features.UpdateReservedName;

public class UpdateReservedNameCommand : IRequest<UpdateReservedNameResponse>
{
    public string OldName { get; set; }
    public string NewName { get; set; }
}
