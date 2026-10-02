using BarkCloud.Identity.Domain;

namespace BarkCloud.Identity.Persistence.Services;

public interface IConfirmationCodesStorage
{
    Task<ConfirmationCode> AddCode(ConfirmationCode confirmationCode);
    Task<ConfirmationCode?> GetCode(Guid id);
    Task<bool> TryReserveAttempt(Guid id, int maxAttempts);
    Task DeleteCode(Guid id);
    Task DeleteByOwnerId(long ownerId);
}
