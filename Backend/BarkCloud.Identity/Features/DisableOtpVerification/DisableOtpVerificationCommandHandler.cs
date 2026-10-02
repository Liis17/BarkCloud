using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Queue.Notifications;

using MediatR;

using OtpNet;


using OtpNotCreatedException = BarkCloud.Identity.Persistence.Exceptions.OtpNotCreatedException;

namespace BarkCloud.Identity.Features.DisableOtpVerification;

public class DisableOtpVerificationCommandHandler : IRequestHandler<DisableOtpVerificationCommand, DisableOtpVerificationResponse>
{
    private readonly UserContext _userContext;
    private readonly IAuthPropertiesStorage _authPropertiesStorage;
    private readonly ReauthPasswordVerifier _reauthPassword;
    private readonly INotificationOutbox _notificationOutbox;
    private readonly RequestContext _requestContext;
    private readonly MetricsCollector _metrics;
    private readonly IAuthRateLimiter _rateLimiter;
    private readonly ILogger<DisableOtpVerificationCommandHandler> _logger;

    public DisableOtpVerificationCommandHandler(UserContext userContext, IAuthPropertiesStorage authPropertiesStorage,
        ReauthPasswordVerifier reauthPassword, INotificationOutbox notificationOutbox, RequestContext requestContext,
        MetricsCollector metrics, IAuthRateLimiter rateLimiter, ILogger<DisableOtpVerificationCommandHandler> logger)
    {
        _userContext = userContext;
        _authPropertiesStorage = authPropertiesStorage;
        _reauthPassword = reauthPassword;
        _notificationOutbox = notificationOutbox;
        _requestContext = requestContext;
        _metrics = metrics;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    public async Task<DisableOtpVerificationResponse> Handle(DisableOtpVerificationCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Начало отключения 2FA для пользователя {UserId}, тип: {OtpType}",
            _userContext.UserId,
            request.OptType
        );

        var otpConfigs = await _authPropertiesStorage.GetUserAuthProperties(_userContext.UserId);

        if (otpConfigs is null)
        {
            _logger.LogWarning(
                "Попытка отключить 2FA для пользователя {UserId}, но настройки не найдены",
                _userContext.UserId
            );
            throw new OtpNotCreatedException();
        }

        string oldMethod = "Неизвестно";
        if (request.OptType == OtpTypeId.Authenticator)
        {
            if (!otpConfigs.OtpEnabled)
            {
                _logger.LogWarning(
                    "Попытка отключить Authenticator 2FA для пользователя {UserId}, но он не включен",
                    _userContext.UserId
                );
                throw new OtpNotCreatedException();
            }

            oldMethod = "Authenticator приложение";

            _logger.LogDebug("Проверка OTP кода для отключения Authenticator 2FA");

            // Шестизначный код под токеном: перебор ограничен по аккаунту.
            await _rateLimiter.EnsureTotpAttemptAsync(_userContext.UserId);

            var totp = new Totp(Base32Encoding.ToBytes(otpConfigs.OtpSecret));

            var isValid = totp.VerifyTotp(request.OtpCode, out long timeStepMatched, VerificationWindow.RfcSpecifiedNetworkDelay);

            if (!isValid)
            {
                _metrics.Increment("otp_authenticator_failed");
                _metrics.Increment("otp_disable_failed");
                _logger.LogWarning(
                    "Неверный OTP код при попытке отключения Authenticator 2FA для пользователя {UserId}",
                    _userContext.UserId
                );
                throw new NotValidOtpCodeException();
            }

            _logger.LogDebug("Отключение Authenticator 2FA для пользователя {UserId}", _userContext.UserId);

            await _authPropertiesStorage.DisableOtp(_userContext.UserId);
            _metrics.Increment("otp_disabled_authenticator");
        }

        if (request.OptType == OtpTypeId.Email)
        {
            // Повторная аутентификация владельца: сессии недостаточно, чтобы снять email-2FA.
            try
            {
                await _reauthPassword.VerifyAsync(_userContext.UserId, request.Password);
            }
            catch (Exception ex) when (ex is InvalidPasswordException or PasswordAttemptsExceededException)
            {
                _metrics.Increment("otp_disable_failed");
                _logger.LogWarning(
                    "Повторная аутентификация паролем отклонена ({Reason}) при отключении Email 2FA для пользователя {UserId}",
                    ex.GetType().Name,
                    _userContext.UserId
                );
                throw;
            }

            _logger.LogDebug("Отключение Email 2FA для пользователя {UserId}", _userContext.UserId);

            oldMethod = "Email";
            await _authPropertiesStorage.DisableEmailOtp(_userContext.UserId);
            _metrics.Increment("otp_disabled_email");
        }

        // Уведомление об отключении 2FA — через outbox: 2FA уже отключена, сбой Users/почты не должен превращаться в ошибку.
        var payload = NotificationPayload.Device(_requestContext);
        payload["old_method"] = oldMethod;
        payload["new_method"] = "Отключена";

        await _notificationOutbox.EnqueueAsync(
            _userContext.UserId,
            NotificationType.TwoFactorMethodChanged,
            "Изменен метод двухфакторной аутентификации",
            payload);

        _logger.LogInformation(
            "2FA успешно отключена для пользователя {UserId}. Метод: {OldMethod}",
            _userContext.UserId,
            oldMethod
        );

        return new DisableOtpVerificationResponse();
    }
}