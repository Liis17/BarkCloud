using BarkCloud.Proto.Identity;

namespace BarkCloud.Drive.Engine;

// Логин (Identity.Auth) + хранение токенов + проактивный авторефрешь (CreateToken)
// за минуту до истечения access-токена. CurrentToken отдаётся интерсептору.
internal sealed class TokenManager(IdentityApi.IdentityApiClient identity, TokenStore store)
{
    private readonly object _lock = new();
    private string? _accessToken;
    private string? _refreshToken;
    private DateTime _accessExpiresUtc;
    private CancellationTokenSource? _refreshCts;

    public bool IsAuthenticated
    {
        get { lock (_lock) return _accessToken != null; }
    }

    public string? CurrentToken
    {
        get { lock (_lock) return _accessToken; }
    }

    public async Task LoginAsync(string login, string password, string? otp)
    {
        var request = new AuthRequest { Password = password, OtpCode = otp ?? string.Empty };
        if (login.Contains('@'))
            request.Email = login;
        else
            request.Username = login;

        var response = await identity.AuthAsync(request);
        ApplyTokens(response.AccessToken, response.RefreshToken);
    }

    // Вход по ключу безопасности — шаг 1: получить challenge/options от сервера (passwordless).
    public async Task<(string OptionsJson, string ChallengeId)> BeginWebAuthnAsync()
    {
        var response = await identity.BeginWebAuthnAssertionAsync(new BeginWebAuthnAssertionRequest());
        return (response.OptionsJson, response.ChallengeId);
    }

    // Вход по ключу безопасности — шаг 2: отправить assertion, получить и сохранить токены.
    public async Task CompleteWebAuthnAsync(string challengeId, string assertionJson)
    {
        var response = await identity.CompleteWebAuthnAssertionAsync(new CompleteWebAuthnAssertionRequest
        {
            ChallengeId = challengeId,
            AssertionJson = assertionJson
        });

        ApplyTokens(response.AccessToken, response.RefreshToken);
    }

    private void ApplyTokens(Token access, Token refresh)
    {
        lock (_lock)
        {
            _accessToken = access.Value;
            _refreshToken = refresh.Value;
            _accessExpiresUtc = access.ExpirationDate?.ToDateTime() ?? DateTime.UtcNow.AddMinutes(5);
        }

        store.SaveRefreshToken(refresh.Value);
        StartRefreshLoop();
    }

    // Выход: обнулить токены, остановить refresh-loop, стереть сохранённый refresh.
    public void Logout()
    {
        lock (_lock)
        {
            _accessToken = null;
            _refreshToken = null; // RefreshLoopAsync увидит refresh==null → break
            _accessExpiresUtc = default;
        }

        _refreshCts?.Cancel();
        _refreshCts = null;
        store.Clear();
    }

    // Молчаливое восстановление сессии на старте движка по сохранённому refresh-токену.
    public async Task TryRestoreAsync()
    {
        var refresh = store.LoadRefreshToken();
        if (string.IsNullOrEmpty(refresh))
            return;

        try
        {
            var response = await identity.CreateTokenAsync(new CreateTokenRequest { RefreshToken = refresh });
            lock (_lock)
            {
                _accessToken = response.AccessToken.Value;
                _refreshToken = refresh;
                _accessExpiresUtc = response.AccessToken.ExpirationDate?.ToDateTime() ?? DateTime.UtcNow.AddMinutes(5);
            }

            StartRefreshLoop();
        }
        catch
        {
            store.Clear(); // refresh недействителен — потребуется повторный вход
        }
    }

    private void StartRefreshLoop()
    {
        _refreshCts?.Cancel();
        var cts = new CancellationTokenSource();
        _refreshCts = cts;
        _ = RefreshLoopAsync(cts.Token);
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                DateTime expires;
                string? refresh;
                lock (_lock)
                {
                    expires = _accessExpiresUtc;
                    refresh = _refreshToken;
                }

                if (refresh is null)
                    break;

                var delay = expires - DateTime.UtcNow - TimeSpan.FromSeconds(60);
                if (delay < TimeSpan.Zero)
                    delay = TimeSpan.Zero;

                await Task.Delay(delay, ct);
                if (ct.IsCancellationRequested)
                    break;

                var response = await identity.CreateTokenAsync(
                    new CreateTokenRequest { RefreshToken = refresh }, cancellationToken: ct);

                lock (_lock)
                {
                    _accessToken = response.AccessToken.Value;
                    _accessExpiresUtc = response.AccessToken.ExpirationDate?.ToDateTime() ?? DateTime.UtcNow.AddMinutes(5);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // штатная отмена (новый логин / выключение)
        }
        catch
        {
            // refresh не удался — считаем сессию недействительной
            lock (_lock) _accessToken = null;
            store.Clear();
        }
    }
}
