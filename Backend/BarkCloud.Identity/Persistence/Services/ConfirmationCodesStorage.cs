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

    public async Task<ConfirmationCode> AddCode(ConfirmationCode confirmationCode)
    {
        _context.ConfirmationCodes.Add(confirmationCode);

        await _context.SaveChangesAsync();

        return confirmationCode;
    }

    public async Task<ConfirmationCode?> GetCode(Guid id)
    {
        return await _context.ConfirmationCodes.FirstOrDefaultAsync(x => x.Id == id);
    }

    /// <summary>
    /// Атомарно занимает одну попытку ввода кода (до сравнения — параллельный перебор не превысит лимит).
    /// Возвращает false, если код не найден, истёк или попытки исчерпаны.
    /// </summary>
    public async Task<bool> TryReserveAttempt(Guid id, int maxAttempts)
    {
        var now = DateTime.UtcNow;

        var reserved = await _context.ConfirmationCodes
            .Where(x => x.Id == id && x.Expires > now && x.Attempts < maxAttempts)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Attempts, x => x.Attempts + 1));

        return reserved == 1;
    }

    public async Task DeleteCode(Guid id)
    {
        var code = await _context.ConfirmationCodes.FirstOrDefaultAsync(x => x.Id == id);
        if (code is null)
            return;

        _context.ConfirmationCodes.Remove(code);
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Удаляет все коды подтверждения пользователя (при удалении аккаунта).
    /// </summary>
    public async Task DeleteByOwnerId(long ownerId)
    {
        await _context.ConfirmationCodes.Where(x => x.OwnerId == ownerId).ExecuteDeleteAsync();
    }
}