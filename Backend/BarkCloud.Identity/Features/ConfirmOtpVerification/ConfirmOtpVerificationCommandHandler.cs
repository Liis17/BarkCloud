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
using OtpType = BarkCloud.Identity.Domain.OtpType;

namespace BarkCloud.Identity.Features.ConfirmOtpVerification;

public class ConfirmOtpVerificationCommandHandler : IRequestHandler<ConfirmOtpVerificationCommand, ConfirmOtpVerificationResponse>
{
    private readonly UserContext _userContext;
    private readonly IAuthPropertiesStorage _authPropertiesStorage;
    private readonly INotificationOutbox _notificationOutbox;
    private readonly RequestContext _requestContext;
    private readonly MetricsCollector _metrics;
    private readonly IAuthRateLimiter _rateLimiter;
    private readonly ILogger<ConfirmOtpVerificationCommandHandler> _logger;

    public ConfirmOtpVerificationCommandHandler(UserContext userContext, IAuthPropertiesStorage authPropertiesStorage,
        INotificationOutbox notificationOutbox, RequestContext requestContext, MetricsCollector metrics,
        IAuthRateLimiter rateLimiter, ILogger<ConfirmOtpVerificationCommandHandler> logger)
    {
        _userContext = userContext;
        _authPropertiesStorage = authPropertiesStorage;
        _notificationOutbox = notificationOutbox;
        _requestContext = requestContext;
        _metrics = metrics;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    public async Task<ConfirmOtpVerificationResponse> Handle(ConfirmOtpVerificationCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Начало подтверждения OTP для пользователя {UserId}",
            _userContext.UserId
        );

        string confirmedMethod;
        string oldMethod = "Отключена";

        try
        {
            var otpConfigs = await _authPropertiesStorage.GetUserAuthProperties(_userContext.UserId);

            // Определяем предыдущий метод 2FA до активации нового
            if (otpConfigs.OtpEnabled) oldMethod = "Authenticator приложение";
            else if (otpConfigs.EmailOtpEnabled) oldMethod = "Email";

            if (otpConfigs.SelectedOtpType == OtpType.Authenticator)
            {
                _logger.LogDebug("Проверка Authenticator OTP кода для пользователя {UserId}", _userContext.UserId);

                // Подтверждаем только ожидающий секрет: действующий OtpSecret до успешной проверки не меняется.
                var pendingSecret = otpConfigs.PendingOtpSecret;

                if (string.IsNullOrEmpty(pendingSecret) || otpConfigs.PendingOtpSecretExpiresAt <= DateTime.UtcNow)
                {
                    _logger.LogWarning(
                        "Нет ожидающего подтверждения секрета Authenticator (не создан или истёк) для пользователя {UserId}",
                        _userContext.UserId
                    );
                    throw new BarkCloud.Shared.Exceptions.Identity.OtpNotCreatedException();
                }

                // Шестизначный код под токеном: перебор ограничен по аккаунту.
                await _rateLimiter.EnsureTotpAttemptAsync(_userContext.UserId);

                var totp = new Totp(Base32Encoding.ToBytes(pendingSecret));

                var isValid = totp.VerifyTotp(request.OtpCode, out long timeStepMatched, VerificationWindow.RfcSpecifiedNetworkDelay);

                if (!isValid)
                {
                    _metrics.Increment("otp_authenticator_failed");
                    _metrics.Increment("otp_confirmation_failed");
                    _logger.LogWarning(
                        "Неверный Authenticator OTP код для пользователя {UserId}",
                        _userContext.UserId
                    );
                    throw new NotValidOtpCodeException();
                }

                _logger.LogDebug("Активация Authenticator OTP для пользователя {UserId}", _userContext.UserId);

                // Атомарно: если секрет успели заменить новым Enable, активировать нечего.
                if (!await _authPropertiesStorage.ActivatePendingOtpSecret(_userContext.UserId, pendingSecret))
                {
                    _metrics.Increment("otp_authenticator_failed");
                    _metrics.Increment("otp_confirmation_failed");
                    _logger.LogWarning(
                        "Ожидающий секрет Authenticator был заменён до подтверждения для пользователя {UserId}",
                        _userContext.UserId
                    );
                    throw new NotValidOtpCodeException();
                }

                confirmedMethod = "Authenticator приложение";

                _metrics.Increment("otp_authenticator_verified");
                _metrics.Increment("otp_enabled_authenticator");

                _logger.LogInformation(
                    "Authenticator OTP успешно активирован для пользователя {UserId}",
                    _userContext.UserId
                );
            }
            else if (otpConfigs.SelectedOtpType == OtpType.Email)
            {
                _logger.LogDebug("Проверка Email OTP кода для пользователя {UserId}", _userContext.UserId);

                if (!await _authPropertiesStorage.TryConsumeEmailAuthCode(
                        _userContext.UserId, Domain.EmailAuthCodePurpose.EnableEmailOtp, request.OtpCode))
                {
                    _metrics.Increment("otp_email_failed");
                    _metrics.Increment("otp_confirmation_failed");
                    _logger.LogWarning(
                        "Неверный Email OTP код для пользователя {UserId}",
                        _userContext.UserId
                    );
                    throw new NotValidOtpCodeException();
                }

                _logger.LogDebug("Активация Email OTP для пользователя {UserId}", _userContext.UserId);

                await _authPropertiesStorage.EnableEmailOtp(_userContext.UserId);
                confirmedMethod = "Email";

                _metrics.Increment("otp_email_verified");
                _metrics.Increment("otp_enabled_email");

                _logger.LogInformation(
                    "Email OTP успешно активирован для пользователя {UserId}",
                    _userContext.UserId
                );
            }
            else
            {
                return new ConfirmOtpVerificationResponse();
            }
        }
        catch (OtpNotCreatedException ex)
        {
            _logger.LogError(
                ex,
                "OTP не был создан для пользователя {UserId}",
                _userContext.UserId
            );
            throw new BarkCloud.Shared.Exceptions.Identity.OtpNotCreatedException();
        }

        // Отправка уведомления об изменении метода 2FA после успешного подтверждения
        await SendTwoFactorChangedNotification(confirmedMethod, oldMethod);

        return new ConfirmOtpVerificationResponse();
    }

    // Метод 2FA уже изменён: письмо ставится в outbox и не может сорвать ответ.
    private async Task SendTwoFactorChangedNotification(string newMethod, string oldMethod)
    {
        var payload = NotificationPayload.Device(_requestContext);
        payload["old_method"] = oldMethod;
        payload["new_method"] = newMethod;

        await _notificationOutbox.EnqueueAsync(
            _userContext.UserId,
            NotificationType.TwoFactorMethodChanged,
            "Изменен метод двухфакторной аутентификации",
            payload);

        _logger.LogInformation(
            "Уведомление об изменении 2FA поставлено в очередь для пользователя {UserId}, новый метод: {NewMethod}",
            _userContext.UserId,
            newMethod
        );
    }
}
