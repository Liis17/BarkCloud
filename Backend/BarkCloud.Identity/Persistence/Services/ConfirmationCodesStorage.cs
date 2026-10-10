using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Persistence.Contexts;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.Persistence.Services;

public class ConfirmationCodesStorage : IConfirmationCodesStorage
{
    private readonly IdentityContext _context;

    public ConfirmationCodesStorage(IdentityContext context)
    {
        _context = context;
    }

    public async Task<ConfirmationCode> AddCode(ConfirmationCode confirmationCode, CancellationToken cancellationToken = default)
    {
        await _context.ConfirmationCodes.AddAsync(confirmationCode, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return confirmationCode;
    }

    public async Task<ConfirmationCode?> GetCode(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.ConfirmationCodes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    /// <summary>
    /// Атомарно занимает одну попытку ввода кода (до сравнения — параллельный перебор не превысит лимит).
    /// Возвращает false, если код не найден, истёк или попытки исчерпаны.
    /// </summary>
    public async Task<bool> TryReserveAttempt(Guid id, int maxAttempts, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        var reserved = await _context.ConfirmationCodes
            .Where(x => x.Id == id && x.Expires > now && x.Attempts < maxAttempts)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Attempts, x => x.Attempts + 1), cancellationToken);

        return reserved == 1;
    }

    public async Task<bool> TryConsumeRegistrationCode(Guid id, long ownerId, string value, DateTime expires,
        int maxAttempts, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var deleted = await _context.ConfirmationCodes
            .Where(x => x.Id == id
                && x.OwnerId == ownerId
                && x.Type == ConfirmationCodeType.Registration
                && x.Value == value
                && x.Expires == expires
                && x.Expires > now
                && x.Attempts <= maxAttempts)
            .ExecuteDeleteAsync(cancellationToken);

        return deleted == 1;
    }

    /// <summary>
    /// Удаляет все коды подтверждения пользователя (при удалении аккаунта).
    /// </summary>
    public async Task DeleteByOwnerId(long ownerId, CancellationToken cancellationToken = default)
    {
        await _context.ConfirmationCodes.Where(x => x.OwnerId == ownerId).ExecuteDeleteAsync(cancellationToken);
    }
}
