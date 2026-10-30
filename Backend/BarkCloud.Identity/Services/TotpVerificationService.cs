using BarkCloud.Identity.Domain;

using OtpNet;

namespace BarkCloud.Identity.Services;

public static class TotpVerificationService
{
    public static ValidatedTotpCode? Verify(long userId, string? secret, long generation, string? code)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(code))
            return null;

        try
        {
            var totp = new Totp(Base32Encoding.ToBytes(secret));
            return totp.VerifyTotp(code, out var timeStep,
                VerificationWindow.RfcSpecifiedNetworkDelay)
                ? new ValidatedTotpCode(userId, secret, generation, timeStep)
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return null;
        }
    }

}
