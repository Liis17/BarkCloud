using BarkCloud.Identity.Domain;
using BarkCloud.Proto.Identity;

using MediatR;

namespace BarkCloud.Identity.Features.ResetPassword;

public class ResetPasswordCommand : IRequest<ResetPasswordResponse>
{
    public string? Email { get; set; }

    public string? Username { get; set; }

    public OtpType OtpType { get; set; }
}