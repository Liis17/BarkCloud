using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.ResolveShare;

public class ResolveShareCommand : IRequest<ResolveShareResponse>
{
    public string Token { get; set; } = string.Empty;
}
