using BarkCloud.Proto.Identity;

using MediatR;

namespace BarkCloud.Identity.Features.ConfirmAccount;

public class ConfirmAccountCommand : IRequest<ConfirmAccountResponse>
{
    public string Code { get; set; }

    public string CodeId { get; set; }

    public string? InitialPassword { get; set; }

    public bool RequiresInitialPassword { get; set; }
}
