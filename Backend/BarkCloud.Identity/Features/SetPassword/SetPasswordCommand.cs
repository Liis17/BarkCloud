namespace BarkCloud.Identity.Features.SetPassword;

using MediatR;

public class SetPasswordCommand : IRequest
{
    public string NewPassword { get; set; }
    public string OldPassword { get; set; }
    public string PermissionToken { get; set; } = string.Empty;
}
