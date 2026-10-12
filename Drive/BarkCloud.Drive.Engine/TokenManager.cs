using BarkCloud.Proto.Identity;

using Grpc.Core;

namespace BarkCloud.Drive.Engine;

// Логин (Identity.Auth) + хранение токенов + проактивный авторефрешь (CreateToken)
// за минуту до истечения access-токена. CurrentToken отдаётся интерсептору.
internal sealed class TokenManager
{
    private static readonly TimeSpan RefreshLeadTime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LogoutDeadline = TimeSpan.FromSeconds(5);

    private readonly IdentityApi.IdentityApiClient _identity;
    private readonly TokenStore _store;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private string? _accessToken;
    private string? _refreshToken;
    private DateTime _accessExpiresUtc;
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _operationCts;
    private long _generation;
    private bool _restorePending;

    public TokenManager(IdentityApi.IdentityApiClient identity, TokenStore store, TimeProvider? time = null)
    {
        _identity = identity;
        _store = store;
        _time = time ?? TimeProvider.System;
    }

    public event Func<Task>? SessionRestored;

    public bool IsAuthenticated
    {
        get { lock (_lock) return _accessToken != null; }
    }

    public string? CurrentToken
    {
        get { lock (_lock) return _accessToken; }
    }

    public async Task<bool> LoginAsync(string login, string password, string? otp)
    {
        var request = new AuthRequest { Password = password, OtpCode = otp ?? string.Empty };
        if (login.Contains('@'))
            request.Email = login;
        else
            request.Username = login;

        var operation = BeginOperation();
        var applied = false;
        try
        {
            var response = await _identity.AuthAsync(request, cancellationToken: operation.Cancellation.Token);
            applied = ApplyTokens(operation.Generation, response.AccessToken, response.RefreshToken);
            return applied;
        }
        finally
        {
            EndOperation(operation, applied);
        }
    }

    // Вход по ключу безопасности — шаг 1: получить challenge/options от сервера (passwordless).
    public async Task<(string OptionsJson, string ChallengeId)> BeginWebAuthnAsync()
    {
        var response = await _identity.BeginWebAuthnAssertionAsync(new BeginWebAuthnAssertionRequest());
        return (response.OptionsJson, response.ChallengeId);
    }

    // Вход по ключу безопасности — шаг 2: отправить assertion, получить и сохранить токены.
    public async Task<bool> CompleteWebAuthnAsync(string challengeId, string assertionJson)
    {
        var operation = BeginOperation();
        var applied = false;
        try
        {
            var response = await _identity.CompleteWebAuthnAssertionAsync(new CompleteWebAuthnAssertionRequest
            {
                ChallengeId = challengeId,
                AssertionJson = assertionJson
            }, cancellationToken: operation.Cancellation.Token);

            applied = ApplyTokens(operation.Generation, response.AccessToken, response.RefreshToken);
            return applied;
        }
        finally
        {
            EndOperation(operation, applied);
        }
    }

    // Молчаливое восстановление сессии на старте движка по сохранённому refresh-токену.
    public async Task TryRestoreAsync()
    {
        var operation = BeginOperation();
        var applied = false;
        try
        {
            var refresh = _store.LoadRefreshToken();
            if (string.IsNullOrEmpty(refresh))
                return;

            lock (_lock)
            {
                if (operation.Generation != _generation)
                    return;

                _accessToken = null;
                _refreshToken = refresh;
                _accessExpiresUtc = _time.GetUtcNow().UtcDateTime;
            }

            var response = await _identity.CreateTokenAsync(
                new CreateTokenRequest { RefreshToken = refresh }, cancellationToken: operation.Cancellation.Token);
            applied = ApplyTokens(operation.Generation, response.AccessToken,
                new Token { Value = refresh });
            if (applied)
                await NotifySessionRestoredAsync(operation.Generation);
        }
        catch (Exception ex) when (IsSessionRejected(ex))
        {
            ClearSession(operation.Generation);
        }
        catch (Exception ex) when (IsOperationCancellation(ex, operation.Cancellation.Token))
        {
            // Новая авторизация, logout или остановка сделали результат восстановления устаревшим.
        }
        catch (Exception ex)
        {
            EngineLog.Error("Восстановление сессии", ex);
            StartRestoreRetry(operation.Generation);
        }
        finally
        {
            EndOperation(operation, applied);
        }
    }

    // Logout инвалидирует текущие операции сразу, сохраняя access-токен для flush/unmount.
    public void BeginLogout()
    {
        CancelCurrentOperations(clearTokens: false, clearStore: false);
    }

    public async Task LogoutRemoteAsync()
    {
        await _identity.LogoutAsync(new LogoutRequest(),
            deadline: _time.GetUtcNow().UtcDateTime.Add(LogoutDeadline));
    }

    // Выход: обнулить токены, остановить refresh-loop, стереть сохранённый refresh.
    public void Logout()
    {
        CancelCurrentOperations(clearTokens: true, clearStore: true);
    }

    // Остановка процесса сохраняет DPAPI refresh-токен для следующего запуска.
    public void Stop()
    {
        CancelCurrentOperations(clearTokens: true, clearStore: false);
    }

    public void ResumeRefreshLoop()
    {
        CancellationTokenSource? refreshCts;
        long generation;
        lock (_lock)
        {
            if (_refreshToken is null || _refreshCts is not null)
                return;

            generation = _generation;
            refreshCts = new CancellationTokenSource();
            _refreshCts = refreshCts;
        }

        _ = RefreshLoopAsync(generation, refreshCts, initialDelay: null);
    }

    internal static bool IsSessionRejected(Exception exception)
        => exception is RpcException rpc
           && (rpc.StatusCode is StatusCode.Unauthenticated or StatusCode.PermissionDenied
               || rpc.StatusCode == StatusCode.FailedPrecondition
               && (ServerErrors.HasCode(rpc, ServerErrors.InvalidRefreshToken)
                   || ServerErrors.HasCode(rpc, ServerErrors.SessionNotFound)));

    private Operation BeginOperation()
    {
        CancellationTokenSource? oldRefresh;
        CancellationTokenSource? oldOperation;
        Operation operation;
        lock (_lock)
        {
            var generation = ++_generation;
            oldRefresh = _refreshCts;
            oldOperation = _operationCts;
            _refreshCts = null;
            var cancellation = new CancellationTokenSource();
            _operationCts = cancellation;
            operation = new Operation(generation, cancellation);
        }

        Cancel(oldRefresh);
        Cancel(oldOperation);
        return operation;
    }

    private bool ApplyTokens(long generation, Token access, Token refresh)
    {
        CancellationTokenSource refreshCts;
        lock (_lock)
        {
            if (generation != _generation)
                return false;

            _store.SaveRefreshToken(refresh.Value);
            _accessToken = access.Value;
            _refreshToken = refresh.Value;
            _accessExpiresUtc = access.ExpirationDate?.ToDateTime()
                ?? _time.GetUtcNow().UtcDateTime.AddMinutes(5);
            _restorePending = false;
            refreshCts = new CancellationTokenSource();
            _refreshCts = refreshCts;
        }

        _ = RefreshLoopAsync(generation, refreshCts, initialDelay: null);
        return true;
    }

    private void EndOperation(Operation operation, bool applied)
    {
        CancellationTokenSource? refreshCts = null;
        lock (_lock)
        {
            if (ReferenceEquals(_operationCts, operation.Cancellation))
                _operationCts = null;

            if (!applied && operation.Generation == _generation
                && _refreshToken is not null && _refreshCts is null)
            {
                refreshCts = new CancellationTokenSource();
                _refreshCts = refreshCts;
            }
        }

        operation.Cancellation.Dispose();
        if (refreshCts is not null)
            _ = RefreshLoopAsync(operation.Generation, refreshCts, initialDelay: null);
    }

    private void StartRestoreRetry(long generation)
    {
        CancellationTokenSource? refreshCts = null;
        lock (_lock)
        {
            if (generation != _generation || _refreshToken is null)
                return;

            _accessToken = null;
            _accessExpiresUtc = _time.GetUtcNow().UtcDateTime;
            _restorePending = true;
            if (_refreshCts is null)
            {
                refreshCts = new CancellationTokenSource();
                _refreshCts = refreshCts;
            }
        }

        if (refreshCts is not null)
            _ = RefreshLoopAsync(generation, refreshCts, InitialRetryDelay);
    }

    private async Task RefreshLoopAsync(long generation, CancellationTokenSource refreshCts,
        TimeSpan? initialDelay)
    {
        var cancellationToken = refreshCts.Token;
        var retryDelay = InitialRetryDelay;
        try
        {
            if (initialDelay is { } initialWait)
            {
                await Task.Delay(initialWait, _time, cancellationToken);
                retryDelay = TimeSpan.FromSeconds(10);
            }

            while (true)
            {
                DateTime expires;
                string? refresh;
                lock (_lock)
                {
                    if (generation != _generation || !ReferenceEquals(_refreshCts, refreshCts))
                        return;
                    expires = _accessExpiresUtc;
                    refresh = _refreshToken;
                }

                if (refresh is null)
                    return;

                var delay = expires - _time.GetUtcNow().UtcDateTime - RefreshLeadTime;
                if (delay > TimeSpan.FromDays(30))
                    delay = TimeSpan.FromDays(30);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, _time, cancellationToken);

                CreateTokenResponse response;
                try
                {
                    response = await _identity.CreateTokenAsync(
                        new CreateTokenRequest { RefreshToken = refresh }, cancellationToken: cancellationToken);
                }
                catch (Exception ex) when (IsSessionRejected(ex))
                {
                    ClearSession(generation, refreshCts);
                    return;
                }
                catch (Exception ex) when (IsOperationCancellation(ex, cancellationToken))
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (!IsRefreshCurrent(generation, refreshCts))
                        return;

                    EngineLog.Error("Обновление сессии", ex);
                    await Task.Delay(retryDelay, _time, cancellationToken);
                    retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, MaximumRetryDelay.Ticks));
                    continue;
                }

                var notifyRestored = false;
                lock (_lock)
                {
                    if (generation != _generation || !ReferenceEquals(_refreshCts, refreshCts))
                        return;

                    _accessToken = response.AccessToken.Value;
                    _accessExpiresUtc = response.AccessToken.ExpirationDate?.ToDateTime()
                        ?? _time.GetUtcNow().UtcDateTime.AddMinutes(5);
                    notifyRestored = _restorePending;
                    _restorePending = false;
                }

                retryDelay = InitialRetryDelay;
                if (notifyRestored)
                    await NotifySessionRestoredAsync(generation);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Штатная отмена при logout, остановке или новой авторизации.
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
        {
            // Отменённый gRPC-вызов также означает штатный выход из цикла.
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_refreshCts, refreshCts))
                    _refreshCts = null;
            }
            refreshCts.Dispose();
        }
    }

    private async Task NotifySessionRestoredAsync(long generation)
    {
        var handlers = SessionRestored;
        if (handlers is null)
            return;

        foreach (Func<Task> handler in handlers.GetInvocationList())
        {
            if (!IsGenerationCurrent(generation))
                return;

            try { await handler(); }
            catch (Exception ex) { EngineLog.Error("Завершение восстановления сессии", ex); }
        }
    }

    private bool IsGenerationCurrent(long generation)
    {
        lock (_lock)
            return generation == _generation;
    }

    private bool IsRefreshCurrent(long generation, CancellationTokenSource refreshCts)
    {
        lock (_lock)
            return generation == _generation && ReferenceEquals(_refreshCts, refreshCts);
    }

    private void ClearSession(long generation, CancellationTokenSource? refreshCts = null)
    {
        lock (_lock)
        {
            if (generation != _generation
                || refreshCts is not null && !ReferenceEquals(_refreshCts, refreshCts))
                return;

            _accessToken = null;
            _refreshToken = null;
            _accessExpiresUtc = default;
            _restorePending = false;
            _store.Clear();
        }
    }

    private void CancelCurrentOperations(bool clearTokens, bool clearStore)
    {
        CancellationTokenSource? refreshCts;
        CancellationTokenSource? operationCts;
        lock (_lock)
        {
            ++_generation;
            refreshCts = _refreshCts;
            operationCts = _operationCts;
            _refreshCts = null;
            _operationCts = null;
            _restorePending = false;
            if (clearTokens)
            {
                _accessToken = null;
                _refreshToken = null;
                _accessExpiresUtc = default;
            }
            if (clearStore)
                _store.Clear();
        }

        Cancel(refreshCts);
        Cancel(operationCts);
    }

    private static bool IsOperationCancellation(Exception exception, CancellationToken cancellationToken)
        => cancellationToken.IsCancellationRequested
           && (exception is OperationCanceledException
               || exception is RpcException { StatusCode: StatusCode.Cancelled });

    private static void Cancel(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private sealed record Operation(long Generation, CancellationTokenSource Cancellation);
}
