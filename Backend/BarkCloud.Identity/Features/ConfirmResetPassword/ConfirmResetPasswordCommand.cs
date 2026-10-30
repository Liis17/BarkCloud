using BarkCloud.Proto.Identity;

using MediatR;

namespace BarkCloud.Identity.Features.ConfirmResetPassword
{
    public class ConfirmResetPasswordCommand : IRequest<ConfirmResetPasswordResponse>
    {
        public Guid ResetId { get; set; }

        public string OtpCode { get; set; }

        public string NewPassword { get; set; }

        public bool RevokeOtherSessions { get; set; } = true;

        public bool ResetPasskeys { get; set; } = true;

        /// <summary>Код Authenticator (TOTP). Требуется для email-сброса при включённом Authenticator.</summary>
        public string? AuthenticatorCode { get; set; }
    }
}
