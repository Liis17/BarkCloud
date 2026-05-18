using BarkCloud.Proto.Identity;

using MediatR;

namespace BarkCloud.Identity.Features.ListOtpVerificationServer;

public class ListOtpVerificationServerCommand : IRequest<ListOtpVerificationResponse>
{
    public long UserId { get; set; }
}
