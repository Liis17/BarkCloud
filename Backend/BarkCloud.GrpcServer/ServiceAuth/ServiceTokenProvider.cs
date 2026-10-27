using BarkCloud.Proto.Identity;
using BarkCloud.Shared.Identity;

using Grpc.Core;

using Microsoft.Extensions.Logging;

namespace BarkCloud.GrpcServer.ServiceAuth;

/// <summary>
/// Получает и кеширует короткоживущие сервисные JWT у Identity (ServiceCredentialsApi).
/// Обновляет токен заранее (за минуту до истечения); при недоступности Identity продолжает
/// работать с ещё действующим кешированным токеном. Заменяет вечные токены из Configuration.
/// </summary>
public sealed class ServiceTokenProvider
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ExchangeDeadline = TimeSpan.FromSeconds(10);

    private readonly ServiceCredentialsApi.ServiceCredentialsApiClient _client;
    private readonly string _credential;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ServiceTokenProvider> _logger;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _exchangeGate = new(1, 1);
    private readonly Dictionary<ServiceId, CacheEntry> _cache = [];

    private sealed record CacheEntry(string Token, DateTimeOffset ExpiresAt);

    public ServiceTokenProvider(
        ServiceCredentialsApi.ServiceCredentialsApiClient client,
        string credential,
        TimeProvider timeProvider,
        ILogger<ServiceTokenProvider> logger)
    {
        _client = client;
        _credential = credential;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Токен для вызова target-сервиса. Горячий путь — чтение кеша; обмен с Identity —
    /// single-flight (одновременные вызовы ждут один обмен, а не устраивают шторм).
    /// </summary>
    public async Task<string> GetTokenAsync(ServiceId target, CancellationToken cancellationToken = default)
    {
        if (TryGetValid(target, out var cached))
            return cached;

        await _exchangeGate.WaitAsync(cancellationToken);
        try
        {
            // Кто-то уже обменял, пока ждали гейт.
            if (TryGetValid(target, out cached))
                return cached;

            try
            {
                var response = await _client.GetServiceTokenAsync(
                    new GetServiceTokenRequest { Credential = _credential, TargetService = (int)target },
                    deadline: _timeProvider.GetUtcNow().Add(ExchangeDeadline).UtcDateTime,
                    cancellationToken: cancellationToken);

                var entry = new CacheEntry(
                    response.Token,
                    response.ExpiresAt.ToDateTimeOffset());

                lock (_sync)
                {
                    _cache[target] = entry;
                }

                return entry.Token;
            }
            catch (RpcException ex)
            {
                // Identity недоступен или отклонил credential: если кешированный токен ещё
                // действует — работаем на нём (деградация доступности, не безопасности).
                CacheEntry? stale;
                lock (_sync)
                {
                    _cache.TryGetValue(target, out var entry);
                    stale = entry;
                }

                if (stale is not null && stale.ExpiresAt > _timeProvider.GetUtcNow())
                {
                    _logger.LogWarning(
                        "Не удалось обновить сервисный токен для {Target} ({Status}); использую кеш до {Expiry}",
                        target, ex.StatusCode, stale.ExpiresAt);
                    return stale.Token;
                }

                throw;
            }
        }
        finally
        {
            _exchangeGate.Release();
        }
    }

    /// <summary>
    /// Синхронная версия для клиентских интерсепторов gRPC: заголовки нужно выставить до вызова
    /// continuation, асинхронных перегрузок у interceptor-делегата нет. На кеше это дёшево;
    /// обмен с Identity блокирует только текущий вызов.
    /// </summary>
    public string GetToken(ServiceId target) =>
        GetTokenAsync(target, CancellationToken.None).GetAwaiter().GetResult();

    private bool TryGetValid(ServiceId target, out string token)
    {
        token = string.Empty;

        lock (_sync)
        {
            if (!_cache.TryGetValue(target, out var entry))
                return false;

            if (entry.ExpiresAt - RefreshMargin <= _timeProvider.GetUtcNow())
                return false;

            token = entry.Token;
            return true;
        }
    }
}
