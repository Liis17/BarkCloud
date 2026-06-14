using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

using BarkCloud.Proto.Identity;
using BarkCloud.Shared.Identity;
using BarkCloud.Web.Infrastructure;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Microsoft.IdentityModel.Tokens;

namespace BarkCloud.Web.Auth;

/// <summary>
/// Авторизация веб-клиента: cookie с токенами, локальная валидация JWT по общему
/// секрету (как и остальные сервисы), refresh и логин через Identity.
/// </summary>
public sealed class AuthGateway
{
    public const string AccessCookie = "bark_at";
    public const string RefreshCookie = "bark_rt";
    public const string DeviceCookie = "bark_did";

    // x-error-code из трейлеров gRPC (см. соответствующие *Exception в Shared.Exceptions)
    private const string ErrOtpNeeded = "C1576884-12D8-4722-A7EE-9F9789AD1265";
    private const string ErrOtpInvalid = "803B632C-4457-4B05-9435-9C3DD0F41E00";
    private const string ErrInvalidLogin = "21BFB9B5-C377-45D1-9B15-6B7F3432B397";

    private readonly IdentityApi.IdentityApiClient _identity;
    private readonly ILogger<AuthGateway> _logger;
    private readonly TokenValidationParameters? _validation;
    private readonly bool _cookieSecure;
    private readonly string _appName;
    private readonly string _appVersion;

    public AuthGateway(IdentityApi.IdentityApiClient identity, IConfiguration configuration, ILogger<AuthGateway> logger)
    {
        _identity = identity;
        _logger = logger;
        _cookieSecure = configuration.Flag("App:CookieSecure");
        _appName = configuration.Value("App:AppName", "BarkCloud Web");
        _appVersion = configuration.Value("App:Version", AppVersion.Current);

        var secret = configuration["JwtSettings:SecretKey"];
        if (string.IsNullOrEmpty(secret))
        {
            _logger.LogError("JwtSettings:SecretKey не задан — валидация токенов невозможна, все запросы будут неавторизованы");
            _validation = null;
        }
        else
        {
            _validation = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(secret)),
                ValidateIssuer = true,
                ValidIssuer = configuration["JwtSettings:Issuer"],
                ValidateAudience = true,
                ValidAudience = configuration["JwtSettings:Audience"],
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            };
        }
    }

    /// <summary>Определяет текущего пользователя по cookie. При истёкшем access-токене пытается обновить его.</summary>
    public async Task<WebUser?> AuthenticateAsync(HttpContext http)
    {
        if (_validation is null)
            return null;

        var access = http.Request.Cookies[AccessCookie];

        if (!string.IsNullOrEmpty(access)
            && TryReadUser(access, out var user, out var expired)
            && !expired)
        {
            return user;
        }

        var refresh = http.Request.Cookies[RefreshCookie];
        if (string.IsNullOrEmpty(refresh))
            return null;

        try
        {
            var response = await _identity.CreateTokenAsync(new CreateTokenRequest { RefreshToken = refresh });
            var newAccess = response.AccessToken;

            if (TryReadUser(newAccess.Value, out var refreshedUser, out var stillExpired) && !stillExpired)
            {
                SetCookie(http, AccessCookie, newAccess.Value, newAccess.ExpirationDate, persistent: http.Request.Cookies[RefreshCookie] != null);
                return refreshedUser;
            }
        }
        catch (RpcException ex)
        {
            _logger.LogInformation("Не удалось обновить токен: {Status} {Detail}", ex.StatusCode, ex.Status.Detail);
        }

        return null;
    }

    public async Task<LoginResult> LoginAsync(HttpContext http, string login, string password, string? otp, bool remember)
    {
        var deviceId = GetOrCreateDeviceId(http);
        var device = BrowserContext.BuildDeviceInfo(http, deviceId, _appName, _appVersion);

        var request = new AuthRequest { Password = password };

        if (LooksLikeEmail(login))
            request.Email = login;
        else
            request.Username = login;

        if (!string.IsNullOrWhiteSpace(otp))
            request.OtpCode = otp;

        try
        {
            var response = await _identity.AuthAsync(request, device.ToMetadata());

            SetCookie(http, AccessCookie, response.AccessToken.Value, response.AccessToken.ExpirationDate, remember);
            SetCookie(http, RefreshCookie, response.RefreshToken.Value, response.RefreshToken.ExpirationDate, remember);

            return new LoginResult(LoginOutcome.Success);
        }
        catch (RpcException ex)
        {
            var code = ex.Trailers.GetValue("x-error-code");

            return code switch
            {
                ErrOtpNeeded => new LoginResult(LoginOutcome.NeedsOtp),
                ErrOtpInvalid => new LoginResult(LoginOutcome.WrongOtp, ex.Status.Detail),
                ErrInvalidLogin => new LoginResult(LoginOutcome.InvalidCredentials, ex.Status.Detail),
                _ => new LoginResult(LoginOutcome.Error, ex.Status.Detail)
            };
        }
    }

    /// <summary>Начать passwordless-вход по ключу: вернуть options и challengeId (логин не нужен —
    /// пользователь определяется самим ключом). null при ошибке сервиса.</summary>
    public async Task<(string OptionsJson, string ChallengeId)?> BeginWebAuthnAsync(HttpContext http)
    {
        var deviceId = GetOrCreateDeviceId(http);
        var device = BrowserContext.BuildDeviceInfo(http, deviceId, _appName, _appVersion);

        try
        {
            var response = await _identity.BeginWebAuthnAssertionAsync(new BeginWebAuthnAssertionRequest(), device.ToMetadata());
            return (response.OptionsJson, response.ChallengeId);
        }
        catch (RpcException ex)
        {
            _logger.LogInformation("WebAuthn begin не выполнен: {Status} {Detail}", ex.StatusCode, ex.Status.Detail);
            return null;
        }
    }

    /// <summary>Завершить вход по ключу безопасности: проверить assertion и выставить cookie сессии.</summary>
    public async Task<LoginResult> CompleteWebAuthnAsync(HttpContext http, string challengeId, string assertionJson, bool remember)
    {
        var deviceId = GetOrCreateDeviceId(http);
        var device = BrowserContext.BuildDeviceInfo(http, deviceId, _appName, _appVersion);

        var request = new CompleteWebAuthnAssertionRequest
        {
            ChallengeId = challengeId,
            AssertionJson = assertionJson
        };

        try
        {
            var response = await _identity.CompleteWebAuthnAssertionAsync(request, device.ToMetadata());

            SetCookie(http, AccessCookie, response.AccessToken.Value, response.AccessToken.ExpirationDate, remember);
            SetCookie(http, RefreshCookie, response.RefreshToken.Value, response.RefreshToken.ExpirationDate, remember);

            return new LoginResult(LoginOutcome.Success);
        }
        catch (RpcException ex)
        {
            _logger.LogInformation("WebAuthn complete не выполнен: {Status} {Detail}", ex.StatusCode, ex.Status.Detail);
            return new LoginResult(LoginOutcome.Error, ex.Status.Detail);
        }
    }

    public async Task LogoutAsync(HttpContext http, WebUser? user)
    {
        if (user is not null)
        {
            try
            {
                await _identity.LogoutAsync(new LogoutRequest(), BrowserContext.UserToken(user.AccessToken));
            }
            catch (RpcException ex)
            {
                _logger.LogInformation("Logout в Identity не выполнен: {Status}", ex.StatusCode);
            }
        }

        Delete(http, AccessCookie);
        Delete(http, RefreshCookie);
    }

    /// <summary>Удаляет cookie сессии без обращения в Identity. Для случаев, когда сессия
    /// уже отозвана на стороне сервера (например, после удаления аккаунта).</summary>
    public void ClearSession(HttpContext http)
    {
        Delete(http, AccessCookie);
        Delete(http, RefreshCookie);
    }

    private bool TryReadUser(string token, out WebUser? user, out bool expired)
    {
        user = null;
        expired = false;

        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };

        try
        {
            var principal = handler.ValidateToken(token, _validation, out _);

            if (principal.FindFirst(IdentityClaims.TokenType)?.Value != TokenType.User.ToString())
                return false;

            var userIdValue = principal.FindFirst(IdentityClaims.UserId)?.Value;
            if (!long.TryParse(userIdValue, out var userId))
                return false;

            user = new WebUser
            {
                UserId = userId,
                DeviceId = principal.FindFirst(IdentityClaims.DeviceId)?.Value,
                AccessToken = token
            };
            return true;
        }
        catch (SecurityTokenExpiredException)
        {
            expired = true;
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Невалидный access-токен");
            return false;
        }
    }

    /// <summary>Выставляет cookie сессии (access/refresh) — общий путь для логина и регистрации.</summary>
    public void IssueSession(HttpContext http, Token access, Token refresh, bool persistent)
    {
        SetCookie(http, AccessCookie, access.Value, access.ExpirationDate, persistent);
        SetCookie(http, RefreshCookie, refresh.Value, refresh.ExpirationDate, persistent);
    }

    public string GetOrCreateDeviceId(HttpContext http)
    {
        var existing = http.Request.Cookies[DeviceCookie];
        if (!string.IsNullOrEmpty(existing))
            return existing;

        var deviceId = Guid.NewGuid().ToString();

        http.Response.Cookies.Append(DeviceCookie, deviceId, new CookieOptions
        {
            HttpOnly = true,
            Secure = _cookieSecure,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.AddYears(5)
        });

        return deviceId;
    }

    private void SetCookie(HttpContext http, string name, string value, Timestamp expiration, bool persistent)
    {
        http.Response.Cookies.Append(name, value, new CookieOptions
        {
            HttpOnly = true,
            Secure = _cookieSecure,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = persistent ? expiration.ToDateTimeOffset() : null
        });
    }

    private void Delete(HttpContext http, string name)
        => http.Response.Cookies.Delete(name, new CookieOptions { Path = "/", Secure = _cookieSecure, SameSite = SameSiteMode.Lax });

    private static bool LooksLikeEmail(string login) => login.Contains('@');
}
