using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Identity;
using BarkCloud.Shared.Queue.Notifications;

using MediatR;

using OtpNet;


using OtpNotCreatedException = BarkCloud.Identity.Persistence.Exceptions.OtpNotCreatedException;

namespace BarkCloud.Identity.Features.DisableOtpVerification;

public class DisableOtpVerificationCommandHandler : IRequestHandler<DisableOtpVerificationCommand, DisableOtpVerificationResponse>
{
    private readonly UserContext _userContext;
    private readonly AuthPropertiesStorage _authPropertiesStorage;
    private readonly NotificationQueueSender _notificationQueueSender;
    private readonly LocationClient _locationClient;
    private readonly UsersServerApi.UsersServerApiClient _usersClient;
    private readonly RequestContext _requestContext;
    private readonly MetricsCollector _metrics;
    private readonly ILogger<DisableOtpVerificationCommandHandler> _logger;

    public DisableOtpVerificationCommandHandler(UserContext userContext, AuthPropertiesStorage authPropertiesStorage,
        NotificationQueueSender notificationQueueSender, LocationClient locationClient,
        UsersServerApi.UsersServerApiClient usersClient, RequestContext requestContext,
        MetricsCollector metrics, ILogger<DisableOtpVerificationCommandHandler> logger)
    {
        _userContext = userContext;
        _authPropertiesStorage = authPropertiesStorage;
        _notificationQueueSender = notificationQueueSender;
        _locationClient = locationClient;
        _usersClient = usersClient;
        _requestContext = requestContext;
        _metrics = metrics;
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
            _logger.LogDebug("Отключение Email 2FA для пользователя {UserId}", _userContext.UserId);

            oldMethod = "Email";
            await _authPropertiesStorage.DisableEmailOtp(_userContext.UserId);
            _metrics.Increment("otp_disabled_email");
        }

        // Отправка уведомления об изменении метода 2FA (отключении)
        var userInfo = await _usersClient.GetByIdAsync(new GetByIdRequest { UserId = _userContext.UserId });
        var userContacts = await _usersClient.GetUserContactsAsync(new GetUserContactsRequest { UserId = _userContext.UserId });

        var locationInfo = await _locationClient.GetLocationString(_requestContext.IpAddress);

        var twoFactorChangedNotification = new EmailNotification
        {
            OwnerId = _userContext.UserId,
            Address = userContacts.Contact.Email,
            CreatedAt = DateTime.UtcNow,
            Payload = new Dictionary<string, string>
            {
                {"username", userInfo.User.Username},
                {"old_method", oldMethod},
                {"new_method", "Отключена"},
                {"ip", _requestContext.IpAddress ?? string.Empty},
                {"devicename", _requestContext.DeviceName ?? string.Empty},
                {"os", _requestContext.OperationSystem ?? string.Empty},
                {"location", locationInfo},
                {"appname", $"{_requestContext.AppName} v.{_requestContext.AppVersion}"},
                {"datetime", DateTime.UtcNow.ToString("dd.MM.yyyy HH:mm:ss")}

            },
            ServiceId = ServiceId.Identity,
            Title = "Изменен метод двухфакторной аутентификации",
            Type = NotificationType.TwoFactorMethodChanged
        };

        _logger.LogDebug(
            "Отправка уведомления об отключении 2FA на адрес {Email}",
            userContacts.Contact.Email
        );

        await _notificationQueueSender.SendNotification(twoFactorChangedNotification);

        _logger.LogInformation(
            "2FA успешно отключена для пользователя {UserId}. Метод: {OldMethod}",
            _userContext.UserId,
            oldMethod
        );

        return new DisableOtpVerificationResponse();
    }
}