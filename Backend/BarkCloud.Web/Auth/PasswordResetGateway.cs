using BarkCloud.Proto.Identity;
using BarkCloud.Web.Infrastructure;

using Grpc.Core;

namespace BarkCloud.Web.Auth;

/// <summary>
/// Восстановление пароля веб-пользователя по коду на почту — через клиентский Identity API:
/// ResetPassword (код на почту) → ConfirmResetPassword (очищает старый пароль, выдаёт сессию)
/// → SetPassword (новый пароль) → сессия.
/// Двухшаговый процесс: <see cref="BeginAsync"/> отправляет код, <see cref="ConfirmAsync"/> подтверждает.
/// </summary>
public sealed class PasswordResetGateway
{
    private const int MinPasswordLength = 6;

    // x-error-code из трейлеров gRPC (см. *Exception в Shared.Exceptions.Identity)
    private const string ErrOtpInvalid = "803B632C-4457-4B05-9435-9C3DD0F41E00";
    private const string ErrResetExpired = "9F3D1B82-8E55-4C71-BD2A-3D7FAC2E6AE1";
    private const string ErrResetNotFound = "5B9A8269-617E-4D4C-9696-A554C59E3A86";
    private const string ErrResetApproved = "BE708516-BF40-44F9-A6D1-A7F30AB02BED";

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
        _appVersion = configuration.Value("App:Version", "v1.0.0");
        _logger = logger;
    }

    /// <summary>Шаг 1: запрашивает код сброса на почту. reset_id возвращается всегда (анти-энумерация).</summary>
    public async Task<PasswordResetResult> BeginAsync(HttpContext http, string login)
    {
        login = login.Trim();
        if (string.IsNullOrWhiteSpace(login))
            return new PasswordResetResult(PasswordResetOutcome.ValidationError, "Укажите почту или юзернейм.");

        var device = BrowserContext.BuildDeviceInfo(http, _auth.GetOrCreateDeviceId(http), _appName, _appVersion);

        var request = new ResetPasswordRequest { OtpType = OtpTypeId.Email };
        if (login.Contains('@'))
            request.Email = login;
        else
            request.Username = login;

        try
        {
            var response = await _identity.ResetPasswordAsync(request, device.ToMetadata());
            _logger.LogInformation("Код сброса пароля отправлен, ResetId {ResetId}", response.ResetId);
            return new PasswordResetResult(PasswordResetOutcome.PendingConfirmation, ResetId: response.ResetId);
        }
        catch (RpcException ex)
        {
            _logger.LogWarning("Сброс пароля (шаг 1) не выполнен: {Status} {Detail}", ex.StatusCode, ex.Status.Detail);
            return new PasswordResetResult(PasswordResetOutcome.Error, "Не удалось отправить код. Попробуйте позже.");
        }
    }

    /// <summary>Шаг 2: проверяет код и устанавливает новый пароль, открывая сессию.</summary>
    public async Task<PasswordResetResult> ConfirmAsync(HttpContext http, string resetId, string code, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(code))
            return new PasswordResetResult(PasswordResetOutcome.CodeInvalid, "Введите код из письма.", ResetId: resetId);

        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < MinPasswordLength)
            return new PasswordResetResult(PasswordResetOutcome.ValidationError,
                $"Пароль должен быть не короче {MinPasswordLength} символов.", ResetId: resetId);

        var device = BrowserContext.BuildDeviceInfo(http, _auth.GetOrCreateDeviceId(http), _appName, _appVersion);

        try
        {
            var confirmed = await _identity.ConfirmResetPasswordAsync(new ConfirmResetPasswordRequest
            {
                ResetId = resetId,
                OtpCode = code.Trim()
            }, device.ToMetadata());

            // ConfirmResetPassword очистил старый хеш — старый пароль не требуется.
            await _identity.SetPasswordAsync(new SetPasswordRequest { Password = newPassword, OldPassword = "" },
                BrowserContext.UserToken(confirmed.AccessToken.Value));

            _auth.IssueSession(http, confirmed.AccessToken, confirmed.RefreshToken, persistent: true);

            _logger.LogInformation("Сброс пароля подтверждён, сессия открыта (ResetId {ResetId})", resetId);
            return new PasswordResetResult(PasswordResetOutcome.Success);
        }
        catch (RpcException ex)
        {
            var outcome = ex.Trailers.GetValue("x-error-code") switch
            {
                ErrOtpInvalid => PasswordResetOutcome.CodeInvalid,
                ErrResetExpired or ErrResetNotFound or ErrResetApproved => PasswordResetOutcome.CodeExpired,
                _ => PasswordResetOutcome.Error
            };
            _logger.LogWarning("Подтверждение сброса пароля не выполнено: {Status} {Detail}", ex.StatusCode, ex.Status.Detail);

            var message = outcome == PasswordResetOutcome.CodeExpired
                ? "Код устарел. Запросите новый."
                : (string.IsNullOrWhiteSpace(ex.Status.Detail) ? "Не удалось подтвердить код." : ex.Status.Detail);

            return new PasswordResetResult(outcome, message, ResetId: resetId);
        }
    }
}
