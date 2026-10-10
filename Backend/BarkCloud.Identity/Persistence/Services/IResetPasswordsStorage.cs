using BarkCloud.Identity.Domain;

namespace BarkCloud.Identity.Persistence.Services;

public interface IResetPasswordsStorage
{
    Task<ResetPassword?> GetResetPassword(Guid resetId, CancellationToken cancellationToken = default);
    Task<ResetPassword> AddResetPassword(ResetPassword resetPassword);
    Task<bool> TryApprove(Guid resetId, CancellationToken cancellationToken = default);
    Task<bool> TryReserveOtpAttempt(Guid resetId, int maxAttempts, CancellationToken cancellationToken = default);
    Task<ResetPassword?> GetActiveEmailReset(long userId);
    Task DeleteByUserId(long userId);
}
