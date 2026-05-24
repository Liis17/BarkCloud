using MediatR;

namespace BarkCloud.Users.Features.ChangeBio;

public class ChangeBioCommand : IRequest
{
    public string? Bio { get; set; }
}
