using BarkCloud.GrpcServer;
using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Identity;
using BarkCloud.Shared.Queue.Notifications;

using MediatR;

using Microsoft.Extensions.Configuration;

using OtpNet;

using QRCoder;


namespace BarkCloud.Identity.Features.EnableOtpVerification;

public class EnableOtpVerificationCommandHandler : IRequestHandler<EnableOtpVerificationCommand, EnableOtpVerificationResponse>
{
    private static readonly TimeSpan PendingSecretLifetime = TimeSpan.FromMinutes(10);

    private readonly UserContext _userContext;
    private readonly IAuthPropertiesStorage _authPropertiesStorage;
    private readonly IPasswordsStorage _passwordsStorage;
    private readonly IdentityContext _context;
    private readonly ReauthPasswordVerifier _reauthPassword;
    private readonly BarkCloud.Proto.Users.UsersServerApi.UsersServerApiClient _usersClient;
    private readonly INotificationOutbox _notificationOutbox;
    private readonly RequestContext _requestContext;
    private readonly MetricsCollector _metrics;
    private readonly IAuthRateLimiter _rateLimiter;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EnableOtpVerificationCommandHandler> _logger;

    public EnableOtpVerificationCommandHandler(UserContext userContext, IAuthPropertiesStorage authPropertiesStorage,
        IPasswordsStorage passwordsStorage, IdentityContext context, ReauthPasswordVerifier reauthPassword,
        UsersServerApi.UsersServerApiClient usersClient,
        INotificationOutbox notificationOutbox, RequestContext requestContext,
        MetricsCollector metrics, IAuthRateLimiter rateLimiter, IConfiguration configuration,
        ILogger<EnableOtpVerificationCommandHandler> logger)
    {
        _userContext = userContext;
        _authPropertiesStorage = authPropertiesStorage;
        _passwordsStorage = passwordsStorage;
        _context = context;
        _reauthPassword = reauthPassword;
        _usersClient = usersClient;
        _notificationOutbox = notificationOutbox;
        _requestContext = requestContext;
        _metrics = metrics;
        _rateLimiter = rateLimiter;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<EnableOtpVerificationResponse> Handle(EnableOtpVerificationCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Начало включения 2FA для пользователя {UserId}, тип: {OtpType}",
            _userContext.UserId,
            request.OptType
        );
        if (string.IsNullOrEmpty(_requestContext.DeviceName))
        {
            throw new XDeviceNameIsRequiredException();
        }

        if (string.IsNullOrEmpty(_requestContext.OperationSystem))
        {
            throw new XOsNameIsRequiredException();
        }

        if (string.IsNullOrEmpty(_requestContext.AppName) || string.IsNullOrEmpty(_requestContext.AppVersion))
        {
            throw new XAppInfoIsRequiedException();
        }

        var userInfo = await _usersClient.GetByIdAsync(new GetByIdRequest() { UserId = _userContext.UserId });

        if (request.OptType == OtpTypeId.Authenticator)
        {
            _logger.LogDebug("Настройка Authenticator 2FA для пользователя {UserId}", _userContext.UserId);

            // Получаем старый метод 2FA
            var oldOptOptions = await _authPropertiesStorage.GetUserAuthProperties(_userContext.UserId, cancellationToken);
            string oldMethod = "Отключена";
            if (oldOptOptions != null)
            {
                if (oldOptOptions.OtpEnabled) oldMethod = "Authenticator приложение";
                else if (oldOptOptions.EmailOtpEnabled) oldMethod = "Email";
            }

            _logger.LogDebug("Предыдущий метод 2FA: {OldMethod}", oldMethod);

            // Повторная аутентификация владельца: пароль всегда, а при замене действующего Authenticator — ещё и его текущий код.
            // Проверки идут до любых записей: отказ не меняет ни активный, ни ожидающий секрет.
            var reauthSecurityVersion = await _passwordsStorage.GetSecurityStateVersion(
                _userContext.UserId, cancellationToken);
            try
            {
                await _reauthPassword.VerifyAsync(_userContext.UserId, request.Password, cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidPasswordException or PasswordAttemptsExceededException)
            {
                _metrics.Increment("otp_setup_failed_invalid_password");
                _logger.LogWarning(
                    "Повторная аутентификация паролем отклонена ({Reason}) при настройке Authenticator 2FA для пользователя {UserId}",
                    ex.GetType().Name,
                    _userContext.UserId
                );
                throw;
            }

            ValidatedEmailAuthCode? validatedEmailCode = null;
            if (oldOptOptions is { OtpEnabled: false, EmailOtpEnabled: true })
            {
                if (string.IsNullOrEmpty(request.CurrentEmailCode))
                {
                    var code = CodeGenerator.GenerateDigitalCode(6);
                    await using var emailTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
                    await IdentityOwnerLock.AcquireAsync(_context, _userContext.UserId, cancellationToken);
                    if (await _passwordsStorage.GetSecurityStateVersion(_userContext.UserId, cancellationToken)
                        != reauthSecurityVersion)
                        throw new ReauthenticationRequiredException();
                    var currentAuthProperties = await _authPropertiesStorage.GetUserAuthPropertiesSnapshot(
                        _userContext.UserId, cancellationToken);
                    if ((currentAuthProperties?.OtpEnabled ?? false) != (oldOptOptions.OtpEnabled)
                        || (currentAuthProperties?.EmailOtpEnabled ?? false) != oldOptOptions.EmailOtpEnabled
                        || currentAuthProperties?.SelectedOtpType != oldOptOptions.SelectedOtpType)
                        throw new ReauthenticationRequiredException();
                    var issue = await _authPropertiesStorage.TryIssueEmailAuthCodeWithSnapshot(
                        _userContext.UserId, Domain.EmailAuthCodePurpose.EnableAuthenticator, code, cancellationToken);
                    if (issue.Sent)
                    {
                        var enqueued = await _notificationOutbox.EnqueueAsync(_userContext.UserId,
                            NotificationType.ConfirmationAuth, "Код подтверждения смены метода 2FA",
                            new Dictionary<string, string>
                            {
                                ["confirmation_code"] = code,
                                ["ip"] = _requestContext.IpAddress ?? string.Empty,
                                ["devicename"] = _requestContext.DeviceName,
                                ["os"] = _requestContext.OperationSystem,
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
                    _userContext.UserId, Domain.EmailAuthCodePurpose.EnableAuthenticator,
                    request.CurrentEmailCode, cancellationToken);
                if (validatedEmailCode is null)
                    throw new NotValidOtpCodeException();
            }
            ValidatedTotpCode? verifiedCurrentTotp = null;
            if (oldOptOptions is { OtpEnabled: true })
            {
                // Шестизначный код под токеном: перебор ограничен по аккаунту.
                await _rateLimiter.EnsureTotpAttemptAsync(_userContext.UserId);
                verifiedCurrentTotp = TotpVerificationService.Verify(_userContext.UserId, oldOptOptions.OtpSecret,
                    oldOptOptions.OtpSecretGeneration, request.CurrentOtpCode);

                if (verifiedCurrentTotp is null)
                {
                    _metrics.Increment("otp_authenticator_failed");
                    _metrics.Increment("otp_setup_failed_invalid_otp");
                    _logger.LogWarning(
                        "Неверный текущий OTP код при замене Authenticator 2FA для пользователя {UserId}",
                        _userContext.UserId
                    );
                    throw new NotValidOtpCodeException();
                }
            }

            var key = KeyGeneration.GenerateRandomKey(20);
            var base32Secret = Base32Encoding.ToString(key);

            var optUri = new OtpUri(OtpNet.OtpType.Totp, base32Secret, userInfo.User.Username, "BarkCloud");

            var uri = optUri.ToString();

            _logger.LogDebug("Генерация QR кода для Authenticator 2FA");

            try
            {
                await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
                await IdentityOwnerLock.AcquireAsync(_context, _userContext.UserId, cancellationToken);
                var securityVersion = await _passwordsStorage.GetSecurityStateVersion(_userContext.UserId, cancellationToken);
                var currentAuthProperties = await _authPropertiesStorage.GetUserAuthPropertiesSnapshot(
                    _userContext.UserId, cancellationToken);
                if (securityVersion != reauthSecurityVersion
                    || (currentAuthProperties?.OtpEnabled ?? false) != (oldOptOptions?.OtpEnabled ?? false)
                    || (currentAuthProperties?.EmailOtpEnabled ?? false) != (oldOptOptions?.EmailOtpEnabled ?? false)
                    || currentAuthProperties?.SelectedOtpType != oldOptOptions?.SelectedOtpType)
                    throw new ReauthenticationRequiredException();
                if (validatedEmailCode is not null
                    && !await _authPropertiesStorage.TryConsumeValidatedEmailAuthCode(validatedEmailCode,
                        cancellationToken))
                    throw new NotValidOtpCodeException();
                if (verifiedCurrentTotp is not null
                    && !await _authPropertiesStorage.TryConsumeTotpStep(verifiedCurrentTotp.UserId,
                        verifiedCurrentTotp.Secret, verifiedCurrentTotp.Generation, verifiedCurrentTotp.TimeStep,
                        cancellationToken))
                    throw new NotValidOtpCodeException();

                // Новый секрет ждёт подтверждения кодом (Confirm) и не заменяет действующий OtpSecret.
                await _authPropertiesStorage.SetPendingOtpSecret(_userContext.UserId, base32Secret,
                    DateTime.UtcNow + PendingSecretLifetime, cancellationToken, securityVersion);
                await _authPropertiesStorage.UpdateOptType(Domain.OtpType.Authenticator, userInfo.User.Id, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                _context.ChangeTracker.Clear();
                throw;
            }

            var qrGenerator = new QRCodeGenerator();
            var qrCodeData = qrGenerator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.Q);

            var qrCode = new Base64QRCode(qrCodeData);

            var qrCodeBase64 = qrCode.GetGraphic(20);

            _metrics.Increment("otp_setup_authenticator");

            _logger.LogInformation(
                "Authenticator 2FA успешно настроен для пользователя {UserId}. Старый метод: {OldMethod}",
                _userContext.UserId,
                oldMethod
            );

            return new EnableOtpVerificationResponse()
            {
                OtpQr = qrCodeBase64,
                OtpCode = base32Secret
            };
        }

        if (request.OptType == OtpTypeId.Email)
        {
            // Режим без почты: включить email-2FA нельзя — код доставить некуда.
            if (!_configuration.EmailEnabled())
            {
                _logger.LogWarning("Включение Email 2FA отклонено — почта на сервере не настроена");
                throw new EmailServiceDisabledException();
            }

            _logger.LogDebug("Настройка Email 2FA для пользователя {UserId}", _userContext.UserId);
            // Получаем старый метод 2FA
            var securityVersion = await _passwordsStorage.GetSecurityStateVersion(_userContext.UserId, cancellationToken);
            var oldOptOptions = await _authPropertiesStorage.GetUserAuthProperties(_userContext.UserId, cancellationToken);
            string oldMethod = "Отключена";
            if (oldOptOptions != null)
            {
                if (oldOptOptions.OtpEnabled) oldMethod = "Authenticator приложение";
                else if (oldOptOptions.EmailOtpEnabled) oldMethod = "Email";
            }

            var userContactInfo = await _usersClient.GetUserContactsAsync(new GetUserContactsRequest { UserId = _userContext.UserId });

            var code = CodeGenerator.GenerateDigitalCode(6);

            _logger.LogDebug("Генерация кода подтверждения для Email 2FA");

            var notificationEnqueued = false;
            EmailAuthCodeIssueResult issue;
            await using (var transaction = await _context.Database.BeginTransactionAsync(cancellationToken))
            {
                await IdentityOwnerLock.AcquireAsync(_context, userContactInfo.User.Id, cancellationToken);
                var currentSecurityVersion = await _passwordsStorage.GetSecurityStateVersion(
                    _userContext.UserId, cancellationToken);
                var currentAuthProperties = await _authPropertiesStorage.GetUserAuthPropertiesSnapshot(
                    _userContext.UserId, cancellationToken);
                if (currentSecurityVersion != securityVersion
                    || (currentAuthProperties?.OtpEnabled ?? false) != (oldOptOptions?.OtpEnabled ?? false)
                    || (currentAuthProperties?.EmailOtpEnabled ?? false) != (oldOptOptions?.EmailOtpEnabled ?? false)
                    || currentAuthProperties?.SelectedOtpType != oldOptOptions?.SelectedOtpType)
                    throw new ReauthenticationRequiredException();

                issue = await _authPropertiesStorage.TryIssueEmailAuthCodeWithSnapshot(
                    userContactInfo.User.Id, Domain.EmailAuthCodePurpose.EnableEmailOtp, code, cancellationToken);
                await _authPropertiesStorage.UpdateOptType(
                    Domain.OtpType.Email, userContactInfo.User.Id, cancellationToken);

                // Код и письмо фиксируются атомарно; при cooldown активная выдача остаётся действующей.
                if (issue.Sent)
                {
                    var payload = NotificationPayload.Device(_requestContext);
                    payload["confirmation_code"] = code;
                    notificationEnqueued = await _notificationOutbox.EnqueueAsync(userContactInfo.User.Id,
                        NotificationType.ConfirmationOtpEmail, "Код подтверждения для привязки", payload,
                        cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
            }

            if (notificationEnqueued)
            {
                _metrics.Increment("notification_outbox_enqueued");
                _metrics.Increment("otp_email_codes_sent");
            }

            _metrics.Increment("otp_setup_email");

            _logger.LogInformation(
                "Email 2FA успешно настроен для пользователя {UserId}. Старый метод: {OldMethod}",
                _userContext.UserId,
                oldMethod
            );

            return new EnableOtpVerificationResponse() { OtpQr = string.Empty };
        }

        _logger.LogWarning(
            "Неизвестный тип OTP: {OtpType} для пользователя {UserId}",
            request.OptType,
            _userContext.UserId
        );

        return new EnableOtpVerificationResponse() { OtpQr = string.Empty }; ;
    }
}
