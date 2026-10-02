using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Persistence.Contexts;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.Persistence.Services;

public class AttemptCountersStorage(IdentityContext context) : IAttemptCountersStorage
{
    // Строки с давно закончившимся окном удаляются при вставке новой — отдельной фоновой очистки нет.
    private static readonly TimeSpan ExpiredRetention = TimeSpan.FromHours(1);

    /// <summary>
    /// Атомарно занимает одну попытку (до самой проверки — параллельный перебор не превысит лимит).
    /// Не более <paramref name="maxAttempts"/> попыток за окно <paramref name="window"/>, которое открывается первой попыткой.
    /// </summary>
    public async Task<AttemptReservation> TryReserve(string key, int maxAttempts, TimeSpan window)
    {
        for (var attempt = 0; ; attempt++)
        {
            var now = DateTime.UtcNow;
            var newWindowEndsAt = now + window;

            var reserved = await context.AuthAttemptCounters
                .Where(x => x.Key == key && (x.WindowEndsAt <= now || x.Count < maxAttempts))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Count, x => x.WindowEndsAt <= now ? 1 : x.Count + 1)
                    .SetProperty(x => x.WindowEndsAt, x => x.WindowEndsAt <= now ? newWindowEndsAt : x.WindowEndsAt));

            if (reserved == 1)
            {
                return new AttemptReservation(true, TimeSpan.Zero);
            }

            // Строка есть, но лимит окна исчерпан.
            var windowEndsAt = await context.AuthAttemptCounters
                .Where(x => x.Key == key)
                .Select(x => (DateTime?)x.WindowEndsAt)
                .FirstOrDefaultAsync();

            if (windowEndsAt is { } endsAt)
            {
                return new AttemptReservation(false, endsAt > now ? endsAt - now : TimeSpan.Zero);
            }

            // Строки ещё нет: создаём сразу с первой занятой попыткой.
            var counter = new AuthAttemptCounter { Key = key, Count = 1, WindowEndsAt = newWindowEndsAt };

            try
            {
                await context.AuthAttemptCounters
                    .Where(x => x.WindowEndsAt < now - ExpiredRetention)
                    .ExecuteDeleteAsync();

                context.AuthAttemptCounters.Add(counter);
                await context.SaveChangesAsync();

                return new AttemptReservation(true, TimeSpan.Zero);
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // Параллельный запрос успел вставить ту же строку — повторяем уже как обновление.
            }
            finally
            {
                // Строкой управляют ExecuteUpdate/ExecuteDelete мимо трекера: не держим её в контексте запроса,
                // иначе повторная вставка того же ключа после Reset упрётся в конфликт идентичности.
                context.Entry(counter).State = EntityState.Detached;
            }
        }
    }

    public async Task Reset(string key)
    {
        await context.AuthAttemptCounters.Where(x => x.Key == key).ExecuteDeleteAsync();
    }
}
