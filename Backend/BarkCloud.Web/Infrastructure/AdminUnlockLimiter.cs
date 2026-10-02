namespace BarkCloud.Web.Infrastructure;

/// <summary>
/// Ограничитель попыток разблокировки раздела «Обслуживание» (admin-пароль открывает управление Docker).
/// Две независимые фиксированные корзины: на адрес источника и общая на всех. Считаются <b>все</b> попытки,
/// успешная разблокировка счётчики не сбрасывает, а ключ не зависит ни от сессии, ни от пользователя —
/// новый вход или новый аккаунт лимит не обходят, а общая корзина не даёт обойти его ротацией адресов.
/// Состояние в памяти: Web работает единственным экземпляром (он же управляет Docker).
/// </summary>
public sealed class AdminUnlockLimiter(TimeProvider? timeProvider = null)
{
    private const int PerIpMaxAttempts = 5;
    private static readonly TimeSpan PerIpWindow = TimeSpan.FromMinutes(15);
    private const int GlobalMaxAttempts = 20;
    private static readonly TimeSpan GlobalWindow = TimeSpan.FromHours(1);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, Bucket> _byIp = new();
    private Bucket _global;

    private struct Bucket
    {
        public int Count;
        public DateTimeOffset EndsAt;
    }

    /// <summary>Занимает попытку. При отказе счётчики не растут; <c>RetryAfter</c> — до конца исчерпанного окна.</summary>
    public (bool Allowed, TimeSpan RetryAfter) TryAcquire(string? sourceIp)
    {
        var key = sourceIp ?? string.Empty;

        lock (_gate)
        {
            var now = _time.GetUtcNow();

            foreach (var expired in _byIp.Where(x => x.Value.EndsAt <= now).Select(x => x.Key).ToList())
            {
                _byIp.Remove(expired);
            }

            if (_global.EndsAt <= now)
            {
                _global = new Bucket { Count = 0, EndsAt = now + GlobalWindow };
            }

            var perIp = _byIp.TryGetValue(key, out var existing)
                ? existing
                : new Bucket { Count = 0, EndsAt = now + PerIpWindow };

            // Общий бюджет проверяется первым: при исчерпании новые записи по адресам не создаются.
            if (_global.Count >= GlobalMaxAttempts)
            {
                return (false, _global.EndsAt - now);
            }

            if (perIp.Count >= PerIpMaxAttempts)
            {
                return (false, perIp.EndsAt - now);
            }

            _global.Count++;
            perIp.Count++;
            _byIp[key] = perIp;

            return (true, TimeSpan.Zero);
        }
    }
}
