using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Queue.Notifications;

using Google.Protobuf.WellKnownTypes;

using MediatR;



namespace BarkCloud.Identity.Features.ConfirmAccount;

public class ConfirmAccountCommandHandler(IConfirmationCodesStorage confirmationCodesStorage,
    UsersServerApi.UsersServerApiClient usersClient, IRefreshTokensStorage refreshTokensStorage, IdentityContext context, RequestContext requestContext,
    INotificationOutbox notificationOutbox, MetricsCollector metrics,
    IRegistrationPolicy registrationPolicy, IAuthRateLimiter rateLimiter,
    ILogger<ConfirmAccountCommandHandler> logger)
    : IRequestHandler<ConfirmAccountCommand, ConfirmAccountResponse>
{

    private const int ExpDaysRefreshToken = 9999;


    public async Task<ConfirmAccountResponse> Handle(ConfirmAccountCommand request, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Начало подтверждения аккаунта. CodeId: {CodeId}",
            request.CodeId
        );
        if (string.IsNullOrEmpty(requestContext.DeviceName))
        {
            throw new XDeviceNameIsRequiredException();
        }

        await registrationPolicy.EnsureRegistrationEnabledAsync(cancellationToken);

        await rateLimiter.EnsureSourceAsync(AuthLimits.ConfirmAccountByIp);

        var codeId = Guid.Parse(request.CodeId);

        logger.LogDebug("Получение кода подтверждения {CodeId}", codeId);

        var code = await confirmationCodesStorage.GetCode(codeId, cancellationToken);

        if (code is null)
        {
            metrics.Increment("account_confirmation_failed");
            metrics.Increment("account_confirmation_failed_not_found");
            logger.LogWarning("Код подтверждения {CodeId} не найден", codeId);
            throw new ConfirmationCodeNotFoundException();
        }

        if (code.Type != ConfirmationCodeType.Registration)
        {
            // Why: защита от использования кода другого типа на endpoint подтверждения регистрации.
            metrics.Increment("account_confirmation_failed");
            metrics.Increment("account_confirmation_failed_not_found");
            logger.LogWarning(
                "Код подтверждения {CodeId} имеет неожиданный тип {Type}, ожидается Registration",
                codeId,
                code.Type
            );
            throw new ConfirmationCodeNotFoundException();
        }

        if (code.Expires < DateTime.UtcNow)
        {
            metrics.Increment("account_confirmation_failed");
            metrics.Increment("account_confirmation_failed_expired");
            logger.LogWarning(
                "Код подтверждения {CodeId} истек. Истек: {ExpirationDate}",
                codeId,
                code.Expires
            );
            throw new ConfirmationCodeExpiredException();
        }

        // Попытка по коду занимается до сравнения (параллельный перебор не превысит лимит) и не зависит от адреса источника.
        if (!await confirmationCodesStorage.TryReserveAttempt(codeId, AuthLimits.ChallengeMaxAttempts, cancellationToken))
        {
            metrics.Increment("account_confirmation_failed");
            metrics.Increment("account_confirmation_failed_attempts_exceeded");
            logger.LogWarning("Исчерпаны попытки ввода кода подтверждения {CodeId}, UserId {UserId}", codeId, code.OwnerId);
            throw new ConfirmationCodeIncorrectException();
        }

        var equals = code.Value.Equals(request.Code, StringComparison.InvariantCultureIgnoreCase);

        if (!equals)
        {
            metrics.Increment("account_confirmation_failed");
            metrics.Increment("account_confirmation_failed_incorrect");
            logger.LogWarning(
                "Неверный код подтверждения для CodeId {CodeId}, UserId {UserId}",
                codeId,
                code.OwnerId
            );
            throw new ConfirmationCodeIncorrectException();
        }

        logger.LogDebug("Подтверждение пользователя {UserId}", code.OwnerId!.Value);

        var confirmRequest = new ConfirmUserRequest { UserId = code.OwnerId!.Value };

        await usersClient.ConfirmUserAsync(confirmRequest, cancellationToken: cancellationToken);

        // Users подтверждает аккаунт вне локального коммита. Повтор RPC безопасен: после сбоя клиента
        // действующий код позволит повторить попытку и получить локальную сессию.
        var refreshTokenString = RefreshTokenGenerator.GenerateRefreshToken();
        var notificationEnqueued = false;

        try
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                if (!await confirmationCodesStorage.TryConsumeRegistrationCode(
                        codeId, code.OwnerId!.Value, code.Value, code.Expires,
                        AuthLimits.ChallengeMaxAttempts, cancellationToken))
                {
                    metrics.Increment("account_confirmation_failed");
                    metrics.Increment("account_confirmation_failed_incorrect");
                    throw new ConfirmationCodeIncorrectException();
                }

                logger.LogDebug("Генерация refresh token для пользователя {UserId}", code.OwnerId.Value);

                await refreshTokensStorage.CreateNewRefreshToken(refreshTokenString, code.OwnerId.Value,
                    requestContext.DeviceId ?? requestContext.DeviceName, ExpDaysRefreshToken, cancellationToken);

                notificationEnqueued = await notificationOutbox.EnqueueAsync(
                    code.OwnerId.Value,
                    NotificationType.SuccessfulRegistration,
                    "Успешная регистрация",
                    NotificationPayload.Device(requestContext),
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
            context.ChangeTracker.Clear();
            throw;
        }

        metrics.Increment("accounts_confirmed");
        metrics.Increment("sessions_created");
        if (notificationEnqueued)
        {
            metrics.Increment("notification_outbox_enqueued");
        }

        logger.LogInformation(
            "Аккаунт успешно подтвержден. UserId: {UserId}, Устройство: {DeviceName}",
            code.OwnerId!.Value,
            requestContext.DeviceName
        );

        return new ConfirmAccountResponse()
        {
            RefreshToken = new Token
            {
                Value = refreshTokenString,
                ExpirationDate = Timestamp.FromDateTime(DateTime.UtcNow.AddDays(ExpDaysRefreshToken))
            }
        };
    }
}
