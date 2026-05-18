using BarkCloud.Proto.Identity;

using MediatR;

namespace BarkCloud.Identity.Features.ConfirmOtpVerification;

public class ConfirmOtpVerificationCommand : IRequest<ConfirmOtpVerificationResponse>
{
    public string OtpCode { get; set; }
}