using BarkCloud.GrpcServer;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Web.Infrastructure;

using Grpc.Core;

namespace BarkCloud.Web.Auth;

/// <summary>
/// Регистрация веб-пользователя с подтверждением по почте — через клиентский Identity API,
/// тем же flow, что и мобильные клиенты:
/// CreateAccount (код на почту) → ConfirmAccount → CreateToken → SetPassword → сессия.
/// Двухшаговый процесс: <see cref="BeginAsync"/> отправляет код, <see cref="ConfirmAsync"/> подтверждает.
/// </summary>
public sealed class RegistrationGateway
{
    private const int MinPasswordLength = 6;

    // x-error-code из трейлеров gRPC (см. *Exception в Shared.Exceptions.Identity)
    private const string ErrCodeIncorrect = "4396D597-D605-4040-AF0F-D9168F0CA034";
    private const string ErrCodeExpired = "7AABF347-1210-4B14-A93B-2BA8574D74E7";
    private const string ErrCodeNotFound = "56D9BB63-DA40-40DE-9C56-7487A1A437D0";

    private readonly UsersServerApi.UsersServerApiClient _users;
    private readonly IdentityApi.IdentityApiClient _identity;
    private readonly AuthGateway _auth;
    private readonly string _appName;
    private readonly string _appVersion;
    private readonly bool _emailEnabled;
    private readonly ILogger<RegistrationGateway> _logger;

    public RegistrationGateway(
        UsersServerApi.UsersServerApiClient users,
        IdentityApi.IdentityApiClient identity,
        AuthGateway auth,
        IConfiguration configuration,
        ILogger<RegistrationGateway> logger)
    {
        _users = users;
        _identity = identity;
        _auth = auth;
        _appName = configuration.Value("App:AppName", "BarkCloud Web");
        _appVersion = configuration.Value("App:Version", "v1.0.0");
        _emailEnabled = configuration.EmailEnabled();
        _logger = logger;
    }

    /// <summary>Шаг 1: валидация, проверка занятости и отправка кода подтверждения на почту.</summary>
    public async Task<RegistrationResult> BeginAsync(
        HttpContext http, string firstName, string lastName, string username, string email, string password)
    {
        username = username.Trim();
        email = email.Trim();

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(email))
            return new RegistrationResult(RegistrationOutcome.ValidationError, "Заполните юзернейм и почту.");

        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            return new RegistrationResult(RegistrationOutcome.ValidationError,
                $"Пароль должен быть не короче {MinPasswordLength} символов.");

        try
        {
            if ((await _users.CheckExistUsernameAsync(new CheckExistUsernameRequest { Username = username })).Exist)
                return new RegistrationResult(RegistrationOutcome.UsernameTaken, "Этот юзернейм уже занят.");

            if ((await _users.CheckExistEmailAsync(new CheckExistEmailRequest { Email = email })).Exist)
                return new RegistrationResult(RegistrationOutcome.EmailTaken, "Аккаунт с такой почтой уже существует.");

            var device = BrowserContext.BuildDeviceInfo(http, _auth.GetOrCreateDeviceId(http), _appName, _appVersion);

            var response = await _identity.CreateAccountAsync(new CreateAccountRequest
            {
                FirstName = firstName.Trim(),
                LastName = lastName.Trim(),
                Username = username,
                Email = email
            }, device.ToMetadata());

            // Режим без почты: Identity сразу создаёт аккаунт и возвращает refresh —
            // подтверждать код не нужно, открываем сессию и ставим пароль сразу.
            if (!_emailEnabled)
            {
                _logger.LogInformation("Почта отключена — мгновенная регистрация {Username}", username);
                return await CompleteAsync(http, response.RefreshToken, password);
            }

            _logger.LogInformation(
                "Код подтверждения отправлен для регистрации {Username}, CodeId {CodeId}", username, response.CodeId);

            return new RegistrationResult(RegistrationOutcome.PendingConfirmation, CodeId: response.CodeId);
        }
        catch (RpcException ex)
        {
            _logger.LogWarning("Регистрация (шаг 1) не выполнена: {Status} {Detail}", ex.StatusCode, ex.Status.Detail);
            return new RegistrationResult(RegistrationOutcome.Error, Friendly(ex, "Не удалось создать аккаунт. Попробуйте позже."));
        }
    }

    /// <summary>Шаг 2: проверяет код, открывает сессию и устанавливает пароль.</summary>
    public async Task<RegistrationResult> ConfirmAsync(HttpContext http, string codeId, string code, string password)
    {
        if (string.IsNullOrWhiteSpace(code))
            return new RegistrationResult(RegistrationOutcome.CodeInvalid, "Введите код из письма.", CodeId: codeId);

        var device = BrowserContext.BuildDeviceInfo(http, _auth.GetOrCreateDeviceId(http), _appName, _appVersion);

        try
        {
            var confirmed = await _identity.ConfirmAccountAsync(new ConfirmAccountRequest
            {
                CodeId = codeId,
                CodeValue = code.Trim()
            }, device.ToMetadata());

            _logger.LogInformation("Регистрация подтверждена, сессия открыта (CodeId {CodeId})", codeId);
            return await CompleteAsync(http, confirmed.RefreshToken, password);
        }
        catch (RpcException ex)
        {
            var outcome = ex.Trailers.GetValue("x-error-code") switch
            {
                ErrCodeIncorrect => RegistrationOutcome.CodeInvalid,
                ErrCodeExpired or ErrCodeNotFound => RegistrationOutcome.CodeExpired,
                _ => RegistrationOutcome.Error
            };
            _logger.LogWarning("Подтверждение регистрации не выполнено: {Status} {Detail}", ex.StatusCode, ex.Status.Detail);
            return new RegistrationResult(outcome, Friendly(ex, "Не удалось подтвердить код."), CodeId: codeId);
        }
    }

    /// <summary>Открывает сессию по refresh-токену и устанавливает пароль (общий хвост для обоих режимов).</summary>
    private async Task<RegistrationResult> CompleteAsync(HttpContext http, Token refreshToken, string password)
    {
        // refresh выдан Identity; получаем access для SetPassword.
        var tokenResponse = await _identity.CreateTokenAsync(new CreateTokenRequest
        {
            RefreshToken = refreshToken.Value
        });
        var access = tokenResponse.AccessToken;

        try
        {
            await _identity.SetPasswordAsync(new SetPasswordRequest { Password = password, OldPassword = "" },
                BrowserContext.UserToken(access.Value));
        }
        catch (RpcException ex)
        {
            // Пользователь уже зарегистрирован и залогинен; пароль можно задать позже в настройках.
            _logger.LogWarning(
                "Не удалось установить пароль при регистрации: {Status} {Detail}", ex.StatusCode, ex.Status.Detail);
        }

        _auth.IssueSession(http, access, refreshToken, persistent: true);

        return new RegistrationResult(RegistrationOutcome.Success);
    }

    private static string Friendly(RpcException ex, string fallback)
        => string.IsNullOrWhiteSpace(ex.Status.Detail) ? fallback : ex.Status.Detail;
}
