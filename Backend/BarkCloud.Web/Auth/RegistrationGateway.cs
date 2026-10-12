using System.ComponentModel.DataAnnotations;

using BarkCloud.GrpcServer;
using BarkCloud.Proto.Identity;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Exceptions.Users;
using BarkCloud.Web.Infrastructure;

using Grpc.Core;

namespace BarkCloud.Web.Auth;

/// <summary>Registration through Identity's atomic initial-password RPCs.</summary>
public sealed class RegistrationGateway
{
    private const int MinPasswordLength = 6;
    private readonly IdentityApi.IdentityApiClient _identity;
    private readonly AuthGateway _auth;
    private readonly string _appName;
    private readonly string _appVersion;
    private readonly bool _emailEnabled;
    private readonly ILogger<RegistrationGateway> _logger;

    public RegistrationGateway(
        IdentityApi.IdentityApiClient identity,
        AuthGateway auth,
        IConfiguration configuration,
        ILogger<RegistrationGateway> logger)
    {
        _identity = identity;
        _auth = auth;
        _appName = configuration.Value("App:AppName", "BarkCloud Web");
        _appVersion = configuration.Value("App:Version", AppVersion.Current);
        _emailEnabled = configuration.EmailEnabled();
        _logger = logger;
    }

    public async Task<RegistrationResult> BeginAsync(
        HttpContext http, string firstName, string lastName, string username, string email, string password,
        CancellationToken cancellationToken = default)
    {
        firstName = firstName.Trim();
        lastName = lastName.Trim();
        username = username.Trim();
        email = email.Trim();

        if (string.IsNullOrWhiteSpace(username))
            return new RegistrationResult(RegistrationOutcome.ValidationError, "Укажите логин.", Field: "username");
        if (string.IsNullOrWhiteSpace(email) || !new EmailAddressAttribute().IsValid(email))
            return new RegistrationResult(RegistrationOutcome.ValidationError, "Укажите корректный адрес почты.", Field: "email");
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            return new RegistrationResult(RegistrationOutcome.ValidationError,
                $"Пароль должен быть не короче {MinPasswordLength} символов.", Field: "password");

        try
        {
            var device = BrowserContext.BuildDeviceInfo(http, _auth.GetOrCreateDeviceId(http), _appName, _appVersion);
            var response = await _identity.CreateAccountWithPasswordAsync(new CreateAccountRequest
            {
                FirstName = firstName,
                LastName = lastName,
                Username = username,
                Email = email,
                InitialPassword = password
            }, device.ToMetadata(), cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // A refresh token means the account already exists, including SMTP-off and protected-draft recovery.
            if (!string.IsNullOrEmpty(response.RefreshToken?.Value))
                return new RegistrationResult(RegistrationOutcome.SessionPending,
                    PendingRefreshToken: response.RefreshToken.Value,
                    PendingRefreshExpiresAt: response.RefreshToken.ExpirationDate.ToDateTimeOffset());

            if (!string.IsNullOrEmpty(response.CodeId))
                return new RegistrationResult(RegistrationOutcome.PendingConfirmation, CodeId: response.CodeId);

            return new RegistrationResult(RegistrationOutcome.Error,
                "Не удалось начать регистрацию. Попробуйте ещё раз.");
        }
        catch (RpcException ex)
        {
            _logger.LogWarning("Регистрация не выполнена: {Status}", ex.StatusCode);
            return MapCreateError(ex, _emailEnabled);
        }
    }

    public async Task<RegistrationResult> ConfirmAsync(
        HttpContext http, string codeId, string code, string password, CancellationToken cancellationToken = default)
    {
        if (!IsSixDigitCode(code))
            return new RegistrationResult(RegistrationOutcome.CodeInvalid, "Введите шестизначный код.", CodeId: codeId, Field: "otp");

        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            return new RegistrationResult(RegistrationOutcome.ValidationError,
                $"Пароль должен быть не короче {MinPasswordLength} символов.", CodeId: codeId, Field: "password");

        var device = BrowserContext.BuildDeviceInfo(http, _auth.GetOrCreateDeviceId(http), _appName, _appVersion);
        try
        {
            var confirmed = await _identity.ConfirmAccountWithPasswordAsync(new ConfirmAccountRequest
            {
                CodeId = codeId,
                CodeValue = code,
                InitialPassword = password
            }, device.ToMetadata(), cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            return string.IsNullOrEmpty(confirmed.RefreshToken?.Value)
                ? new RegistrationResult(RegistrationOutcome.Error, "Не удалось завершить регистрацию. Попробуйте ещё раз.", CodeId: codeId)
                : new RegistrationResult(RegistrationOutcome.SessionPending,
                    PendingRefreshToken: confirmed.RefreshToken.Value,
                    PendingRefreshExpiresAt: confirmed.RefreshToken.ExpirationDate.ToDateTimeOffset());
        }
        catch (RpcException ex)
        {
            _logger.LogWarning("Подтверждение регистрации не выполнено: {Status}", ex.StatusCode);
            var codeValue = ex.Trailers.GetValue("x-error-code");
            if (codeValue == new ConfirmationCodeIncorrectException().ErrorCode)
                return new RegistrationResult(RegistrationOutcome.CodeInvalid, "Код неверен.", CodeId: codeId);
            if (codeValue == new ConfirmationCodeExpiredException().ErrorCode
                || codeValue == new ConfirmationCodeNotFoundException().ErrorCode)
                return new RegistrationResult(RegistrationOutcome.CodeExpired, "Код недействителен или истёк. Запросите новый.", CodeId: codeId);
            if (ex.StatusCode == StatusCode.Unimplemented)
                return new RegistrationResult(RegistrationOutcome.Error, "Регистрация временно недоступна. Попробуйте позже.", CodeId: codeId);
            if (codeValue == new InvalidLoginOrPasswordException().ErrorCode
                || codeValue == new PasswordAttemptsExceededException().ErrorCode)
                return new RegistrationResult(RegistrationOutcome.Error, "Не удалось подтвердить регистрацию. Проверьте пароль и попробуйте позже.", CodeId: codeId);
            return new RegistrationResult(RegistrationOutcome.Error, "Не удалось подтвердить регистрацию. Попробуйте позже.", CodeId: codeId);
        }
    }

    public async Task<bool> IssueSessionFromRefreshAsync(HttpContext http, string refreshToken, DateTimeOffset refreshExpiresAt,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var tokens = await _identity.CreateTokenAsync(new CreateTokenRequest { RefreshToken = refreshToken },
                cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(tokens.AccessToken?.Value))
                return false;

            _auth.IssueSession(http, tokens.AccessToken, new Token
            {
                Value = refreshToken,
                ExpirationDate = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(refreshExpiresAt)
            }, persistent: true);
            return true;
        }
        catch (RpcException ex)
        {
            _logger.LogWarning("Выдача сессии после регистрации не выполнена: {Status}", ex.StatusCode);
            return false;
        }
    }

    private static RegistrationResult MapCreateError(RpcException exception, bool emailEnabled)
    {
        var code = exception.Trailers.GetValue("x-error-code");
        if (exception.StatusCode == StatusCode.Unimplemented)
            return new RegistrationResult(RegistrationOutcome.Error, "Регистрация временно недоступна. Попробуйте позже.");
        if (code == new UsernameExistException().ErrorCode)
            return new RegistrationResult(RegistrationOutcome.UsernameTaken, "Этот логин уже занят.", Field: "username");
        if (code == new EmailExistException().ErrorCode)
            return new RegistrationResult(RegistrationOutcome.EmailTaken, "Аккаунт с такой почтой уже существует.", Field: "email");
        if (code == new UsernameReservedException().ErrorCode)
            return new RegistrationResult(RegistrationOutcome.UsernameTaken, "Это имя пользователя нельзя использовать.", Field: "username");
        if (code == new UserIsDraftException().ErrorCode)
            return new RegistrationResult(RegistrationOutcome.Error,
                "Регистрация с этими данными ещё не завершена. Повторите её с той же почтой и паролем.");
        if (code == new InvalidLoginOrPasswordException().ErrorCode)
            return new RegistrationResult(RegistrationOutcome.ValidationError,
                "Почта или пароль не совпадают с незавершённой регистрацией.", Field: "password");
        if (code == new PasswordAttemptsExceededException().ErrorCode)
            return new RegistrationResult(RegistrationOutcome.Error, "Слишком много попыток. Попробуйте позже.");
        if (!emailEnabled)
            return new RegistrationResult(RegistrationOutcome.Error,
                "Регистрация не завершилась. Повторите её с той же почтой и паролем, чтобы завершить создание аккаунта.");
        return new RegistrationResult(RegistrationOutcome.Error, "Не удалось создать аккаунт. Попробуйте позже.");
    }

    private static bool IsSixDigitCode(string? code)
        => code is { Length: 6 } && code.All(character => character is >= '0' and <= '9');
}
