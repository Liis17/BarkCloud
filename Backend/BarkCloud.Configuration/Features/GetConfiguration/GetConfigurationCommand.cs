using BarkCloud.Proto.Configuration;
using BarkCloud.Shared.Identity;

using MediatR;

namespace BarkCloud.Configuration.Features.GetConfiguration;

public class GetConfigurationCommand : IRequest<GetConfigurationResponse>
{
    public ServiceId ServiceId { get; set; }
}