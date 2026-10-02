using BarkCloud.Identity.Domain;

namespace BarkCloud.Identity.Persistence.Services;

public interface IResetPasswordsStorage
{
    Task<ResetPassword?> GetResetPassword(Guid resetId);
    Task<ResetPassword> AddResetPassword(ResetPassword resetPassword);
    Task<bool> TryApprove(Guid resetId);
    Task<bool> TryReserveOtpAttempt(Guid resetId, int maxAttempts);
    Task<ResetPassword?> GetActiveEmailReset(long userId);
    Task DeleteByUserId(long userId);
}
