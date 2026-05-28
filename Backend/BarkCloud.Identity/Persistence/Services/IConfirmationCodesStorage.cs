using BarkCloud.Identity.Domain;

namespace BarkCloud.Identity.Persistence.Services;

public interface IConfirmationCodesStorage
{
    Task<ConfirmationCode> AddCode(ConfirmationCode confirmationCode);
    Task<ConfirmationCode?> GetCode(Guid id);
    Task DeleteCode(Guid id);
    Task DeleteByOwnerId(long ownerId);
}
