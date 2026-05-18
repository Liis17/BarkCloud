using BarkCloud.Proto.Identity;

using MediatR;

namespace BarkCloud.Identity.Features.DisableOtpVerificationServer;

public class DisableOtpVerificationServerCommand : IRequest<DisableOtpVerificationResponse>
{
    public long UserId { get; set; }

    public OtpTypeId OtpType { get; set; }
}
