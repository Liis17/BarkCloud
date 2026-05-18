using BarkCloud.Proto.Identity;

using MediatR;

namespace BarkCloud.Identity.Features.EnableOtpVerification;

public class EnableOtpVerificationCommand : IRequest<EnableOtpVerificationResponse>
{
    public OtpTypeId OptType { get; set; }
}