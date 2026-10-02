using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Services;

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
    /// Атомарно занимает одну попытку ввода кода (до сравнения — параллельный перебор не превысит лимит).
    /// Возвращает false, если запрос уже использован, истёк или попытки исчерпаны.
    /// </summary>
    public async Task<bool> TryReserveOtpAttempt(Guid resetId, int maxAttempts)
    {
        var now = DateTime.UtcNow;

        var reserved = await context.ResetPasswords
            .Where(x => x.Id == resetId && !x.IsApproved && x.ExpiresAt > now && x.OtpAttempts < maxAttempts)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.OtpAttempts, x => x.OtpAttempts + 1));

        return reserved == 1;
    }

    /// <summary>
    /// Последний действующий запрос сброса по email-коду: не использован, не истёк, попытки не исчерпаны
    /// (на него можно ответить повторным запросом, не отправляя новое письмо).
    /// </summary>
    public async Task<ResetPassword?> GetActiveEmailReset(long userId)
    {
        var now = DateTime.UtcNow;

        return await context.ResetPasswords
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.OtpType == OtpType.Email && !x.IsApproved && x.ExpiresAt > now
                        && x.OtpAttempts < AuthLimits.ChallengeMaxAttempts)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Удаляет все запросы на сброс пароля пользователя (при удалении аккаунта).
    /// </summary>
    public async Task DeleteByUserId(long userId)
    {
        await context.ResetPasswords.Where(x => x.UserId == userId).ExecuteDeleteAsync();
    }
}