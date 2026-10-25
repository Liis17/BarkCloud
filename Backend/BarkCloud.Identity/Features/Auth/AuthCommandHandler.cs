using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Identity;
using BarkCloud.Shared.Queue.Notifications;

using MediatR;

namespace BarkCloud.Identity.Features.Auth;

public class AuthCommandHandler(UsersServerApi.UsersServerApiClient usersClient,
    IAuthPropertiesStorage authPropertiesStorage, NotificationQueueSender notificationQueueSender,
    INotificationOutbox notificationOutbox, SessionIssuer sessionIssuer, RequestContext requestContext,
    IPasswordsStorage passwordsStorage, LocationClient locationClient, MetricsCollector metrics,
    IAuthRateLimiter rateLimiter, ILogger<AuthCommandHandler> logger) : IRequestHandler<AuthCommand, AuthResponse>
{
    public async Task<AuthResponse> Handle(AuthCommand request, CancellationToken cancellationToken)
    {
        var login = request.Username ?? request.Email;

        logger.LogInformation(
            "Попытка входа: {Login} с устройства {DeviceName}",
            login,
            requestContext.DeviceName
        );

        if (string.IsNullOrEmpty(request.Username) && string.IsNullOrEmpty(request.Email))
        {
            logger.LogWarning("Попытка входа без указания логина или email");
            throw new NotSetUsernameOrEmailException();
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            logger.LogWarning("Попытка входа без пароля для {Login}", login);
            throw new InvalidLoginOrPasswordException();
        }

        if (string.IsNullOrEmpty(requestContext.DeviceName))
        {
            throw new XDeviceNameIsRequiredException();
        }

        if (string.IsNullOrEmpty(requestContext.OperationSystem))
        {
            throw new XOsNameIsRequiredException();
        }

        if (string.IsNullOrEmpty(requestContext.AppName) || string.IsNullOrEmpty(requestContext.AppVersion))
        {
            throw new XAppInfoIsRequiedException();
        }

        // Лимит по источнику — до обращения к Users: перебор логинов с одного адреса не доходит ни до поиска, ни до bcrypt.
        await rateLimiter.EnsureSourceAsync(AuthLimits.AuthByIp);

        var usersRequest = new FindByLoginRequest();

        if (!string.IsNullOrEmpty(request.Username))
        {
            usersRequest.Username = request.Username;
        }
        else
        {
            usersRequest.Email = request.Email;
        }

        logger.LogDebug("Поиск пользователя по логину: {Login}", login);

        FindByLoginResponse user;
        try
        {
            user = await usersClient.FindByLoginAsync(usersRequest, cancellationToken: cancellationToken);
        }
        catch (UserNotFoundException)
        {
            RecordMissingUser(login);
            throw new InvalidLoginOrPasswordException();
        }

        if (user.User is null)
        {
            RecordMissingUser(login);
            throw new InvalidLoginOrPasswordException();
        }

        // Попытка по аккаунту занимается до проверки пароля/кода (параллельный перебор не превысит лимит) и не зависит
        // от адреса источника. Сбрасывается только полным успешным входом.
        var accountKey = user.User.Id.ToString();

        if (!await rateLimiter.TryReserveAsync(AuthLimits.LoginByAccount, accountKey))
        {
            metrics.Increment("auth_login_failed");
            metrics.Increment("auth_login_failed_locked");
            logger.LogWarning(
                "Вход заблокирован лимитом попыток для пользователя {UserId}. Логин: {Login}, IP: {IpAddress}",
                user.User.Id,
                login,
                requestContext.IpAddress
            );
            throw new PasswordAttemptsExceededException();
        }

        // Пароль проверяется первым: без него нельзя ни получить код 2FA, ни узнать, что 2FA включена.
        logger.LogDebug("Проверка пароля для пользователя {UserId}", user.User.Id);

        var securityStateVersion = await passwordsStorage.GetSecurityStateVersion(user.User.Id, cancellationToken);
        var currentPasswordHash = await passwordsStorage.GetUserPasswordHash(user.User.Id, cancellationToken);

        if (!PasswordHasher.VerifyPassword(request.Password, currentPasswordHash))
        {
            metrics.Increment("auth_login_failed");
            metrics.Increment("auth_login_failed_invalid_password");
            logger.LogWarning(
                "Неудачная попытка входа: неверный пароль для пользователя {UserId}. Логин: {Login}, IP: {IpAddress}",
                user.User.Id,
                login,
                requestContext.IpAddress
            );

            // Письмо о неудачной попытке — не чаще раза в окно на аккаунт: иначе перебор превращается в рассылку жертве.
            if (!await rateLimiter.TryReserveAsync(AuthLimits.FailedLoginMail, accountKey))
            {
                throw new InvalidLoginOrPasswordException();
            }

            // Письмо уходит через outbox: ни Users, ни геолокация не задерживают отказ и не меняют его результат.
            try
            {
                var notificationEnqueued = await notificationOutbox.EnqueueAsync(
                    user.User.Id,
                    NotificationType.FailedLogin,
                    "Неуспешная попытка входа в аккаунт",
                    NotificationPayload.Device(requestContext));
                if (notificationEnqueued)
                {
                    metrics.Increment("notification_outbox_enqueued");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Не удалось поставить в outbox уведомление о неудачном входе для пользователя {UserId}",
                    user.User.Id);
            }

            throw new InvalidLoginOrPasswordException();
        }

        var optOptions = await authPropertiesStorage.GetUserAuthProperties(user.User.Id, cancellationToken);

        if (optOptions != null && (optOptions.EmailOtpEnabled || optOptions.OtpEnabled) && string.IsNullOrEmpty(request.OtpCode))
        {
            logger.LogInformation(
                "Требуется OTP код для пользователя {UserId}. Email OTP: {EmailOtp}, App OTP: {AppOtp}",
                user.User.Id,
                optOptions.EmailOtpEnabled,
                optOptions.OtpEnabled
            );

            if (optOptions is { OtpEnabled: false, EmailOtpEnabled: true })
            {
                logger.LogDebug("Генерация и отправка Email OTP кода для пользователя {UserId}", user.User.Id);

                var userContactInfo = await usersClient.GetUserContactsAsync(new GetUserContactsRequest { UserId = user.User.Id });
                var code = CodeGenerator.GenerateDigitalCode(6);

                // false — прежний код выдан совсем недавно и остаётся в силе: письмо не отправляем.
                var issue = await authPropertiesStorage.TryIssueEmailAuthCodeWithSnapshot(
                    userContactInfo.User.Id, EmailAuthCodePurpose.Login, code, cancellationToken);
                if (issue.Sent)
                {
                    // Получаем данные о местоположении IP-адреса
                    var locationInfo = await locationClient.GetLocationString(requestContext.IpAddress);

                    var emailNotification = new EmailNotification
                    {
                        OwnerId = userContactInfo.User.Id,
                        Address = userContactInfo.Contact.Email,
                        CreatedAt = DateTime.UtcNow,
                        Payload = new Dictionary<string, string>
                        {
                            {"username", userContactInfo.User.Username},
                            {"confirmation_code", code},
                            {"ip", requestContext.IpAddress ?? string.Empty},
                            {"devicename", requestContext.DeviceName},
                            {"os", requestContext.OperationSystem},
                            {"location", locationInfo},
                            {"appname", $"{requestContext.AppName} v.{requestContext.AppVersion}"},
                            {"datetime", DateTime.UtcNow.ToString("dd.MM.yyyy HH:mm:ss")}
                        },
                        ServiceId = ServiceId.Identity,
                        Title = "Код подтверждения для входа",
                        Type = NotificationType.ConfirmationAuth
                    };

                    await notificationQueueSender.SendNotification(emailNotification);
                    metrics.Increment("otp_email_codes_sent");
                }
                else
                {
                    logger.LogInformation(
                        "Повторная отправка Email OTP кода для пользователя {UserId} пропущена: действует ранее выданный код",
                        user.User.Id
                    );
                }

                metrics.Increment("auth_otp_required");
                throw new OtpCodeNeedException("email", issue.RetryAfterSeconds,
                    issue.Sent ? "sent" : "existing");
            }

            metrics.Increment("auth_otp_required");
            throw new OtpCodeNeedException("authenticator");
        }

        ValidatedTotpCode? validatedTotp = null;
        if (optOptions is { OtpEnabled: true })
        {
            logger.LogDebug("Проверка TOTP кода для пользователя {UserId}", user.User.Id);
            await rateLimiter.EnsureTotpAttemptAsync(user.User.Id);
            validatedTotp = TotpVerificationService.Verify(user.User.Id, optOptions.OtpSecret,
                optOptions.OtpSecretGeneration, request.OtpCode);

            if (validatedTotp is null)
            {
                metrics.Increment("auth_login_failed");
                metrics.Increment("otp_authenticator_failed");
                logger.LogWarning(
                    "Неверный TOTP код для пользователя {UserId}, IP: {IpAddress}",
                    user.User.Id,
                    requestContext.IpAddress
                );
                throw new NotValidOtpCodeException();
            }

        }

        ValidatedEmailAuthCode? validatedEmailCode = null;
        if (optOptions is { OtpEnabled: false, EmailOtpEnabled: true })
        {
            logger.LogDebug("Проверка Email OTP кода для пользователя {UserId}", user.User.Id);

            validatedEmailCode = await authPropertiesStorage.TryValidateAndReserveEmailAuthCode(
                user.User.Id, EmailAuthCodePurpose.Login, request.OtpCode, cancellationToken);
            if (validatedEmailCode is null)
            {
                metrics.Increment("auth_login_failed");
                metrics.Increment("otp_email_failed");
                logger.LogWarning(
                    "Неверный Email OTP код для пользователя {UserId}, IP: {IpAddress}",
                    user.User.Id,
                    requestContext.IpAddress
                );
                throw new NotValidOtpCodeException();
            }

        }

        var response = await sessionIssuer.IssueAsync(user.User.Id, cancellationToken, async transactionToken =>
        {
            if (await passwordsStorage.GetSecurityStateVersion(user.User.Id, transactionToken)
                != securityStateVersion)
                throw new InvalidLoginOrPasswordException();

            var currentAuthProperties = await authPropertiesStorage.GetUserAuthProperties(user.User.Id,
                transactionToken);
            if ((currentAuthProperties?.OtpEnabled ?? false) != (optOptions?.OtpEnabled ?? false)
                || (currentAuthProperties?.EmailOtpEnabled ?? false) != (optOptions?.EmailOtpEnabled ?? false))
                throw new InvalidLoginOrPasswordException();

            if (validatedTotp is not null
                && !await authPropertiesStorage.TryConsumeTotpStep(validatedTotp.UserId, validatedTotp.Secret,
                    validatedTotp.Generation, validatedTotp.TimeStep, transactionToken))
            {
                throw new NotValidOtpCodeException();
            }

            if (validatedEmailCode is not null
                && !await authPropertiesStorage.TryConsumeValidatedEmailAuthCode(validatedEmailCode, transactionToken))
            {
                throw new NotValidOtpCodeException();
            }

            await rateLimiter.ResetAsync(AuthLimits.LoginByAccount, accountKey, transactionToken);
        });

        if (optOptions is { OtpEnabled: true })
        {
            metrics.Increment("otp_authenticator_verified");
            logger.LogDebug("TOTP код успешно проверен для пользователя {UserId}", user.User.Id);
        }

        if (validatedEmailCode is not null)
        {
            metrics.Increment("otp_email_verified");
            logger.LogDebug("Email OTP код успешно проверен для пользователя {UserId}", user.User.Id);
        }

        logger.LogInformation(
            "Успешная аутентификация пользователя {UserId} ({Login}) с устройства {DeviceName}, IP: {IpAddress}",
            user.User.Id,
            login,
            requestContext.DeviceName,
            requestContext.IpAddress
        );

        return response;
    }

    private void RecordMissingUser(string? login)
    {
        metrics.Increment("auth_login_failed");
        metrics.Increment("auth_login_failed_user_not_found");
        logger.LogWarning(
            "Неудачная попытка входа: пользователь не найден. Логин: {Login}, IP: {IpAddress}",
            login,
            requestContext.IpAddress);
    }
}
