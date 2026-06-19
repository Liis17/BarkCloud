using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Exceptions.Users;
using BarkCloud.GrpcServer;
using BarkCloud.Shared.Identity;
using BarkCloud.Shared.Queue.Notifications;

using Google.Protobuf.WellKnownTypes;

using MediatR;

using Microsoft.Extensions.Configuration;


namespace BarkCloud.Identity.Features.CreateAccount;

public class CreateAccountCommandHandler(UsersServerApi.UsersServerApiClient usersClient,
    IConfirmationCodesStorage confirationCodesStorage, NotificationQueueSender notificationQueueSender,
    RequestContext requestContext, LocationClient locationClient, MetricsCollector metrics,
    IRefreshTokensStorage refreshTokensStorage, IConfiguration configuration, IRegistrationPolicy registrationPolicy,
    ILogger<CreateAccountCommandHandler> logger)
    : IRequestHandler<CreateAccountCommand, CreateAccountResponse>
{
    private const int ExpDaysRefreshToken = 9999;

    public async Task<CreateAccountResponse> Handle(CreateAccountCommand request, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Начало создания аккаунта. Username: {Username}, Email: {Email}",
            request.Username,
            request.Email
        );
        if (string.IsNullOrEmpty(request.Email) || string.IsNullOrEmpty(request.Username))
        {
            throw new UsernameOrEmailIsEmptyException();
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

        await registrationPolicy.EnsureRegistrationEnabledAsync(cancellationToken);

        var createAccountRequest = new AddDraftUserRequest()
        {
            Email = request.Email?.Trim(),
            Username = request.Username?.Trim(),
            FirstName = request.FirstName?.Trim(),
            LastName = request.LastName?.Trim()
        };

        logger.LogDebug("Создание черновика пользователя {Username}", request.Username);

        AddDraftUserResponse responseUser = null;

        try
        {
            responseUser = await usersClient.AddDraftUserAsync(createAccountRequest);
            metrics.Increment("accounts_drafted");
            logger.LogDebug("Черновик пользователя создан. UserId: {UserId}", responseUser.UserId);
        }
        catch (UserIsDraftException)
        {
            metrics.Increment("accounts_draft_overridden");
            logger.LogDebug("Пользователь уже существует как черновик, переопределение данных");
            responseUser = await usersClient.OverrideDraftUserAsync(createAccountRequest);
        }

        // Режим без почты: код подтверждения отправить некуда — создаём аккаунт сразу.
        // Подтверждаем черновик и выдаём refresh-токен, минуя ConfirmAccount. Письмо не шлём.
        if (!configuration.EmailEnabled())
        {
            logger.LogInformation(
                "Почта отключена — мгновенное создание аккаунта без подтверждения. UserId: {UserId}",
                responseUser.UserId);

            await usersClient.ConfirmUserAsync(new ConfirmUserRequest { UserId = responseUser.UserId });

            var instantRefreshToken = RefreshTokenGenerator.GenerateRefreshToken();
            await refreshTokensStorage.CreateNewRefreshToken(
                instantRefreshToken,
                responseUser.UserId,
                requestContext.DeviceId ?? requestContext.DeviceName,
                ExpDaysRefreshToken);

            metrics.Increment("accounts_confirmed");
            metrics.Increment("sessions_created");

            return new CreateAccountResponse
            {
                RefreshToken = new Token
                {
                    Value = instantRefreshToken,
                    ExpirationDate = Timestamp.FromDateTime(DateTime.UtcNow.AddDays(ExpDaysRefreshToken))
                }
            };
        }

        var code = CodeGenerator.GenerateDigitalCode(6);

        logger.LogDebug("Генерация кода подтверждения для регистрации");

        var confirmationCode = new ConfirmationCode()
        {
            Expires = DateTime.UtcNow.AddHours(6),
            OwnerId = responseUser.UserId,
            Type = ConfirmationCodeType.Registration,
            Value = code
        };

        confirmationCode = await confirationCodesStorage.AddCode(confirmationCode);

        var locationInfo = await locationClient.GetLocationString(requestContext.IpAddress);

        var payload = new Dictionary<string, string>()
        {
            { "confirmation_code", code },
            { "username", request.Username },
            { "ip", requestContext.IpAddress ?? string.Empty },
            {"devicename", requestContext.DeviceName },
            {"os", requestContext.OperationSystem},
            {"location", locationInfo},
            {"appname", $"{requestContext.AppName} v.{requestContext.AppVersion}"},
            {"datetime", DateTime.UtcNow.ToString("dd.MM.yyyy HH:mm:ss")}
        };

        logger.LogDebug(
            "Отправка кода подтверждения на адрес {Email} для пользователя {UserId}",
            request.Email,
            responseUser.UserId
        );

        await notificationQueueSender.SendNotification(new EmailNotification()
        {
            Address = request.Email,
            CreatedAt = DateTime.UtcNow,
            OwnerId = responseUser.UserId,
            ServiceId = ServiceId.Identity,
            Payload = payload,
            Title = "Код подтверждения",
            Type = NotificationType.ConfirmationRegistration,
        });

        logger.LogInformation(
            "Аккаунт создан. UserId: {UserId}, Username: {Username}, Email: {Email}, CodeId: {CodeId}",
            responseUser.UserId,
            request.Username,
            request.Email,
            confirmationCode.Id
        );

        return new CreateAccountResponse { CodeId = confirmationCode.Id.ToString() };
    }
}