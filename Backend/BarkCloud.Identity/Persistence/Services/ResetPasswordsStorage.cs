using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Persistence.Contexts;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.Persistence.Services;

public class ResetPasswordsStorage(IdentityContext context) : IResetPasswordsStorage
{
    public async Task<ResetPassword?> GetResetPassword(Guid resetId)
    {
        return await context.ResetPasswords.FirstOrDefaultAsync(x => x.Id == resetId);
    }

    public async Task<ResetPassword> AddResetPassword(ResetPassword resetPassword)
    {
        context.ResetPasswords.Add(resetPassword);

        await context.SaveChangesAsync();

        return resetPassword;
    }

    /// <summary>
    /// Атомарно помечает запрос на сброс пароля как использованный. Возвращает false, если запрос
    /// уже был использован (в т.ч. параллельным подтверждением) — успех получает только один вызов.
    /// </summary>
    /// <param name="resetId">Идентификатор запроса сброса</param>
    public async Task<bool> TryApprove(Guid resetId)
    {
        var updated = await context.ResetPasswords
            .Where(x => x.Id == resetId && !x.IsApproved)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, true));

        return updated == 1;
    }

    /// <summary>
    /// Удаляет все запросы на сброс пароля пользователя (при удалении аккаунта).
    /// </summary>
    public async Task DeleteByUserId(long userId)
    {
        await context.ResetPasswords.Where(x => x.UserId == userId).ExecuteDeleteAsync();
    }
}