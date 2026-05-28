using BarkCloud.Identity.Domain;

namespace BarkCloud.Identity.Persistence.Services;

public interface IResetPasswordsStorage
{
    Task<ResetPassword?> GetResetPassword(Guid resetId);
    Task<ResetPassword> AddResetPassword(ResetPassword resetPassword);
    Task SetApproved(Guid resetId);
    Task DeleteByUserId(long userId);
}
