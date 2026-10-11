using BarkCloud.GrpcServer;
using BarkCloud.Proto.Identity;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Web.Infrastructure;

using Grpc.Core;

namespace BarkCloud.Web.Auth;

/// <summary>HTTP-facing password-reset operations routed through Identity.</summary>
public sealed class PasswordResetGateway
{
    private const int MinPasswordLength = 6;
    private const string ErrOtpInvalid = "803B632C-4457-4B05-9435-9C3DD0F41E00";
    private const string ErrResetExpired = "9F3D1B82-8E55-4C71-BD2A-3D7FAC2E6AE1";
    private const string ErrResetNotFound = "5B9A8269-617E-4D4C-9696-A554C59E3A86";
    private const string ErrResetApproved = "BE708516-BF40-44F9-A6D1-A7F30AB02BED";
    private const string ErrNewPasswordSameAsOld = "730737E2-64C9-492B-BE0C-459191C13F76";
    private const string ErrTooManyRequests = "8F2B6D41-5A93-4C7E-B0D8-1E4A7C9F3B26";

    private readonly IdentityApi.IdentityApiClient _identity;
    private readonly AuthGateway _auth;
    private readonly string _appName;
    private readonly string _appVersion;
    private readonly ILogger<PasswordResetGateway> _logger;

    public PasswordResetGateway(
        IdentityApi.IdentityApiClient identity,
        AuthGateway auth,
        IConfiguration configuration,
        ILogger<PasswordResetGateway> logger)
    {
        _identity = identity;
        _auth = auth;
        _appName = configuration.Value("App:AppName", "BarkCloud Web");
        _appVersion = configuration.Value("App:Version", AppVersion.Current);
        _logger = logger;
    }

    public async Task<PasswordResetResult> BeginAsync(HttpContext http, string login,
        CancellationToken cancellationToken = default)
    {
        login = login.Trim();
        if (string.IsNullOrWhiteSpace(login))
            return new PasswordResetResult(PasswordResetOutcome.ValidationError, "Укажите почту или логин.");

        var device = BrowserContext.BuildDeviceInfo(http, _auth.GetOrCreateDeviceId(http), _appName, _appVersion);
        var request = new ResetPasswordRequest { OtpType = OtpTypeId.Email };
        if (login.Contains('@'))
            request.Email = login;
        else
            request.Username = login;

        try
        {
            var response = await _identity.ResetPasswordAsync(request, device.ToMetadata(), cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new PasswordResetResult(PasswordResetOutcome.PendingConfirmation,
                "Если аккаунт существует, мы отправим письмо с кодом для восстановления.", response.ResetId);
        }
        catch (RpcException ex)
        {
            _logger.LogWarning("Запрос сброса пароля не выполнен: {Status}", ex.StatusCode);
            if (ex.Trailers.GetValue("x-error-code") == ErrTooManyRequests)
                return new PasswordResetResult(PasswordResetOutcome.TooManyAttempts,
                    "Слишком много запросов. Попробуйте позже.", RetryAfterSeconds: ReadRetryAfter(ex));

            return new PasswordResetResult(PasswordResetOutcome.Error,
                "Не удалось начать восстановление. Попробуйте позже.");
        }
    }

    public async Task<PasswordResetResult> ConfirmAsync(
        HttpContext http, string resetId, string code, string newPassword, bool revokeOtherSessions,
        CancellationToken cancellationToken = default)
    {
        if (!IsSixDigitCode(code))
            return new PasswordResetResult(PasswordResetOutcome.CodeInvalid,
                "Код недействителен или истёк. Запросите новый.", ResetId: resetId);

        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < MinPasswordLength)
            return new PasswordResetResult(PasswordResetOutcome.ValidationError,
                $"Пароль должен быть не короче {MinPasswordLength} символов.", ResetId: resetId);

        var device = BrowserContext.BuildDeviceInfo(http, _auth.GetOrCreateDeviceId(http), _appName, _appVersion);
        try
        {
            var confirmed = await _identity.ConfirmResetPasswordAsync(new ConfirmResetPasswordRequest
            {
                ResetId = resetId,
                OtpCode = code,
                NewPassword = newPassword,
                RevokeOtherSessions = revokeOtherSessions
            }, device.ToMetadata(), cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            _auth.IssueSession(http, confirmed.AccessToken, confirmed.RefreshToken, persistent: true);
            return new PasswordResetResult(PasswordResetOutcome.Success);
        }
        catch (RpcException ex)
        {
            _logger.LogWarning("Подтверждение сброса пароля не выполнено: {Status}", ex.StatusCode);
            var codeValue = ex.Trailers.GetValue("x-error-code");
            if (codeValue == ErrTooManyRequests)
                return new PasswordResetResult(PasswordResetOutcome.TooManyAttempts,
                    "Слишком много попыток. Попробуйте позже.", ResetId: resetId,
                    RetryAfterSeconds: ReadRetryAfter(ex));

            if (codeValue is ErrOtpInvalid or ErrResetExpired or ErrResetNotFound or ErrResetApproved)
                return new PasswordResetResult(PasswordResetOutcome.CodeInvalid,
                    "Код недействителен или истёк. Запросите новый.", ResetId: resetId);

            if (codeValue == ErrNewPasswordSameAsOld)
                return new PasswordResetResult(PasswordResetOutcome.ValidationError,
                    "Новый пароль должен отличаться от текущего.", ResetId: resetId);

            return new PasswordResetResult(PasswordResetOutcome.Error,
                "Не удалось подтвердить код. Попробуйте позже.", ResetId: resetId);
        }
    }

    private static bool IsSixDigitCode(string? code)
        => code is { Length: 6 } && code.All(character => character is >= '0' and <= '9');

    private static int? ReadRetryAfter(RpcException exception)
        => int.TryParse(exception.Trailers.GetValue("x-retry-after-seconds"), out var seconds)
            ? Math.Clamp(seconds, 1, 3600)
            : null;
}
