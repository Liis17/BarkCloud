using BarkCloud.Identity.Domain;

namespace BarkCloud.Identity.Persistence.Services;

public interface IConfirmationCodesStorage
{
    Task<ConfirmationCode> AddCode(ConfirmationCode confirmationCode, CancellationToken cancellationToken = default);
    Task<ConfirmationCode?> GetCode(Guid id, CancellationToken cancellationToken = default);
    Task<bool> TryReserveAttempt(Guid id, int maxAttempts, CancellationToken cancellationToken = default);
    Task<bool> TryConsumeRegistrationCode(Guid id, long ownerId, string value, DateTime expires, int maxAttempts,
        CancellationToken cancellationToken = default);
    Task DeleteByOwnerId(long ownerId, CancellationToken cancellationToken = default);
}
