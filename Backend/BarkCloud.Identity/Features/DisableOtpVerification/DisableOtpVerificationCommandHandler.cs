using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Proto.Identity;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Queue.Notifications;

using MediatR;

using OtpNotCreatedException = BarkCloud.Identity.Persistence.Exceptions.OtpNotCreatedException;

namespace BarkCloud.Identity.Features.DisableOtpVerification;

public class DisableOtpVerificationCommandHandler : IRequestHandler<DisableOtpVerificationCommand, DisableOtpVerificationResponse>
{
    private readonly UserContext _userContext;
    private readonly IdentityContext _context;
    private readonly IAuthPropertiesStorage _authPropertiesStorage;
    private readonly IPasswordsStorage _passwordsStorage;
    private readonly ReauthPasswordVerifier _reauthPassword;
    private readonly INotificationOutbox _notificationOutbox;
    private readonly RequestContext _requestContext;
    private readonly MetricsCollector _metrics;
    private readonly IAuthRateLimiter _rateLimiter;
    private readonly ILogger<DisableOtpVerificationCommandHandler> _logger;

    public DisableOtpVerificationCommandHandler(UserContext userContext, IdentityContext context, IAuthPropertiesStorage authPropertiesStorage,
        IPasswordsStorage passwordsStorage, ReauthPasswordVerifier reauthPassword, INotificationOutbox notificationOutbox, RequestContext requestContext,
        MetricsCollector metrics, IAuthRateLimiter rateLimiter, ILogger<DisableOtpVerificationCommandHandler> logger)
    {
        _userContext = userContext;
        _context = context;
        _authPropertiesStorage = authPropertiesStorage;
        _passwordsStorage = passwordsStorage;
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

        var otpConfigs = await _authPropertiesStorage.GetUserAuthProperties(_userContext.UserId, cancellationToken);

        if (otpConfigs is null)
        {
            _logger.LogWarning(
                "Попытка отключить 2FA для пользователя {UserId}, но настройки не найдены",
                _userContext.UserId
            );
            throw new OtpNotCreatedException();
        }

        string oldMethod = "Неизвестно";
        var disableAuthenticator = false;
        var disableEmail = false;
        long? reauthSecurityVersion = null;
        ValidatedTotpCode? validatedTotp = null;
        ValidatedEmailAuthCode? validatedEmailCode = null;
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

            validatedTotp = TotpVerificationService.Verify(_userContext.UserId, otpConfigs.OtpSecret,
                otpConfigs.OtpSecretGeneration, request.OtpCode);

            if (validatedTotp is null)
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

            disableAuthenticator = true;
        }

        if (request.OptType == OtpTypeId.Email)
        {
            // Повторная аутентификация владельца: сессии недостаточно, чтобы снять email-2FA.
            reauthSecurityVersion = await _passwordsStorage.GetSecurityStateVersion(_userContext.UserId, cancellationToken);
            try
            {
                await _reauthPassword.VerifyAsync(_userContext.UserId, request.Password, cancellationToken);
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

            if (otpConfigs.EmailOtpEnabled)
            {
                if (string.IsNullOrEmpty(request.CurrentEmailCode))
                {
                    var code = CodeGenerator.GenerateDigitalCode(6);
                    await using var emailTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
                    await IdentityOwnerLock.AcquireAsync(_context, _userContext.UserId, cancellationToken);
                    if (await _passwordsStorage.GetSecurityStateVersion(_userContext.UserId, cancellationToken)
                        != reauthSecurityVersion)
                        throw new ReauthenticationRequiredException();
                    var issue = await _authPropertiesStorage.TryIssueEmailAuthCodeWithSnapshot(
                        _userContext.UserId, EmailAuthCodePurpose.DisableEmailOtp, code, cancellationToken);
                    if (issue.Sent)
                    {
                        var enqueued = await _notificationOutbox.EnqueueAsync(_userContext.UserId,
                            NotificationType.ConfirmationAuth, "Код подтверждения отключения Email 2FA",
                            new Dictionary<string, string>
                            {
                                ["confirmation_code"] = code,
                                ["ip"] = _requestContext.IpAddress ?? string.Empty,
                                ["devicename"] = _requestContext.DeviceName ?? string.Empty,
                                ["os"] = _requestContext.OperationSystem ?? string.Empty,
                                ["appname"] = $"{_requestContext.AppName} v.{_requestContext.AppVersion}",
                                ["datetime"] = DateTime.UtcNow.ToString("dd.MM.yyyy HH:mm:ss")
                            }, cancellationToken);
                        if (enqueued) _metrics.Increment("notification_outbox_enqueued");
                    }
                    await emailTransaction.CommitAsync(cancellationToken);
                    throw new OtpCodeNeedException("email", issue.RetryAfterSeconds,
                        issue.Sent ? "sent" : "existing");
                }

                validatedEmailCode = await _authPropertiesStorage.TryValidateAndReserveEmailAuthCode(
                    _userContext.UserId, EmailAuthCodePurpose.DisableEmailOtp, request.CurrentEmailCode,
                    cancellationToken);
                if (validatedEmailCode is null)
                    throw new NotValidOtpCodeException();
            }

            _logger.LogDebug("Отключение Email 2FA для пользователя {UserId}", _userContext.UserId);

            oldMethod = "Email";
            disableEmail = true;
        }

        var payload = NotificationPayload.Device(_requestContext);
        payload["old_method"] = oldMethod;
        payload["new_method"] = "Отключена";
        var notificationEnqueued = false;

        try
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                await IdentityOwnerLock.AcquireAsync(_context, _userContext.UserId, cancellationToken);
                if (disableEmail)
                {
                    var currentAuthProperties = await _authPropertiesStorage.GetUserAuthProperties(
                        _userContext.UserId, cancellationToken);
                    if (currentAuthProperties is not { EmailOtpEnabled: true }
                        || await _passwordsStorage.GetSecurityStateVersion(_userContext.UserId, cancellationToken)
                            != reauthSecurityVersion)
                        throw new ReauthenticationRequiredException();
                }
                if (validatedEmailCode is not null
                    && !await _authPropertiesStorage.TryConsumeValidatedEmailAuthCode(validatedEmailCode,
                        cancellationToken))
                    throw new NotValidOtpCodeException();
                if (validatedTotp is not null
                    && !await _authPropertiesStorage.TryConsumeTotpStep(validatedTotp.UserId,
                        validatedTotp.Secret, validatedTotp.Generation, validatedTotp.TimeStep, cancellationToken))
                    throw new NotValidOtpCodeException();

                if (disableAuthenticator)
                {
                    await _authPropertiesStorage.DisableOtp(_userContext.UserId, cancellationToken);
                }
                else if (disableEmail)
                {
                    await _authPropertiesStorage.DisableEmailOtp(_userContext.UserId, cancellationToken);
                }

                notificationEnqueued = await _notificationOutbox.EnqueueAsync(
                    _userContext.UserId,
                    NotificationType.TwoFactorMethodChanged,
                    "Изменен метод двухфакторной аутентификации",
                    payload,
                    cancellationToken);

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
                throw;
            }
        }
        catch
        {
            _context.ChangeTracker.Clear();
            throw;
        }

        if (notificationEnqueued)
        {
            _metrics.Increment("notification_outbox_enqueued");
        }

        if (disableAuthenticator)
        {
            _metrics.Increment("otp_disabled_authenticator");
        }
        else if (disableEmail)
        {
            _metrics.Increment("otp_disabled_email");
        }

        _logger.LogInformation(
            "2FA успешно отключена для пользователя {UserId}. Метод: {OldMethod}",
            _userContext.UserId,
            oldMethod
        );

        return new DisableOtpVerificationResponse();
    }
}
