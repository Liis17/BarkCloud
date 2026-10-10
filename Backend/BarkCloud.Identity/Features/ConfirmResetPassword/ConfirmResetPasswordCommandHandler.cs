using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Persistence.Services;
using System.Security.Cryptography;
using System.Text;

using BarkCloud.Shared.Exceptions.Identity;

using MediatR;

using OtpNet;

using OtpType = BarkCloud.Identity.Domain.OtpType;

namespace BarkCloud.Identity.Features.ConfirmResetPassword
{
    using CreateToken;

    using Google.Protobuf.WellKnownTypes;

    using GrpcServer.Tracker;

    using Proto.Identity;

    using Services;

    public class ConfirmResetPasswordCommandHandler : IRequestHandler<ConfirmResetPasswordCommand, ConfirmResetPasswordResponse>
    {
        private readonly IResetPasswordsStorage _resetPasswordsStorage;
        private readonly IAuthPropertiesStorage _authPropertiesStorage;
        private readonly IPasswordsStorage _passwordsStorage;
        private readonly IRefreshTokensStorage refreshTokensStorage;
        private readonly IMediator _mediator;
        private readonly PasswordChangedNotifier _passwordChangedNotifier;
        private readonly RequestContext requestContext;
        private readonly MetricsCollector _metrics;
        private readonly IAuthRateLimiter _rateLimiter;
        private readonly ILogger<ConfirmResetPasswordCommandHandler> _logger;
        private readonly IdentityContext _context;

        private const int ExpDaysRefreshToken = 9999;


        public ConfirmResetPasswordCommandHandler(IResetPasswordsStorage resetPasswordsStorage, IAuthPropertiesStorage authPropertiesStorage,
            IPasswordsStorage passwordsStorage, IRefreshTokensStorage refreshTokensStorage, IMediator mediator,
            PasswordChangedNotifier passwordChangedNotifier,
            RequestContext requestContext, MetricsCollector metrics, IAuthRateLimiter rateLimiter,
            ILogger<ConfirmResetPasswordCommandHandler> logger, IdentityContext context)
        {
            _resetPasswordsStorage = resetPasswordsStorage;
            _authPropertiesStorage = authPropertiesStorage;
            _passwordsStorage = passwordsStorage;
            this.refreshTokensStorage = refreshTokensStorage;
            _mediator = mediator;
            _passwordChangedNotifier = passwordChangedNotifier;
            this.requestContext = requestContext;
            _metrics = metrics;
            _rateLimiter = rateLimiter;
            _logger = logger;
            _context = context;
        }

        public async Task<ConfirmResetPasswordResponse> Handle(ConfirmResetPasswordCommand request, CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "Начало подтверждения сброса пароля. ResetId: {ResetId}",
                request.ResetId
            );
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

            await _rateLimiter.EnsureSourceAsync(AuthLimits.ConfirmResetPasswordByIp);

            var resetPasswordInfo = await _resetPasswordsStorage.GetResetPassword(request.ResetId, cancellationToken);

            if (resetPasswordInfo is null)
            {
                _metrics.Increment("password_reset_confirmation_failed");
                _metrics.Increment("password_reset_confirmation_failed_not_found");
                _logger.LogWarning("Reset ID {ResetId} не найден", request.ResetId);
                throw new ResetIdNotFoundException();
            }

            if (resetPasswordInfo.IsApproved)
            {
                _metrics.Increment("password_reset_confirmation_failed");
                _metrics.Increment("password_reset_confirmation_failed_already_used");
                _logger.LogWarning(
                    "Reset ID {ResetId} уже был использован для пользователя {UserId}",
                    request.ResetId,
                    resetPasswordInfo.UserId
                );
                throw new ResetIdHasIsApprovedException();
            }

            if (resetPasswordInfo.ExpiresAt < DateTime.UtcNow)
            {
                _metrics.Increment("password_reset_confirmation_failed");
                _metrics.Increment("password_reset_confirmation_failed_expired");
                _logger.LogWarning(
                    "Reset ID {ResetId} истёк для пользователя {UserId}. ExpiresAt: {ExpiresAt}",
                    request.ResetId,
                    resetPasswordInfo.UserId,
                    resetPasswordInfo.ExpiresAt
                );
                throw new ResetIdExpiredException();
            }

            _logger.LogDebug(
                "Проверка OTP кода для пользователя {UserId}, тип OTP: {OtpType}",
                resetPasswordInfo.UserId,
                resetPasswordInfo.OtpType
            );

            // Попытка по этому запросу занимается до сравнения кода (параллельный перебор не превысит лимит)
            // и не зависит от адреса источника: исчерпав попытки, код надо запрашивать заново.
            if (!await _resetPasswordsStorage.TryReserveOtpAttempt(request.ResetId, AuthLimits.ChallengeMaxAttempts, cancellationToken))
            {
                _metrics.Increment("password_reset_confirmation_failed");
                _metrics.Increment("password_reset_confirmation_failed_attempts_exceeded");
                _logger.LogWarning(
                    "Исчерпаны попытки ввода кода для Reset ID {ResetId}, пользователь {UserId}",
                    request.ResetId,
                    resetPasswordInfo.UserId
                );
                throw new NotValidOtpCodeException();
            }

            if (resetPasswordInfo.OtpType == OtpType.Authenticator)
            {
                var otpSecret = await _authPropertiesStorage.GetOtpSecretKey(resetPasswordInfo.UserId, cancellationToken);

                var totp = new Totp(Base32Encoding.ToBytes(otpSecret));

                var isValid = totp.VerifyTotp(request.OtpCode, out long timeStepMatched, VerificationWindow.RfcSpecifiedNetworkDelay);

                if (!isValid)
                {
                    _metrics.Increment("password_reset_confirmation_failed");
                    _metrics.Increment("otp_authenticator_failed");
                    _logger.LogWarning(
                        "Неверный Authenticator OTP код для пользователя {UserId}",
                        resetPasswordInfo.UserId
                    );
                    throw new NotValidOtpCodeException();
                }

                _metrics.Increment("otp_authenticator_verified");
                _logger.LogDebug("Authenticator OTP код успешно проверен для пользователя {UserId}", resetPasswordInfo.UserId);
            }
            else
            {
                if (!FixedTimeEquals(resetPasswordInfo.OtpCode, request.OtpCode))
                {
                    _metrics.Increment("password_reset_confirmation_failed");
                    _metrics.Increment("otp_email_failed");
                    _logger.LogWarning(
                        "Неверный Email OTP код для пользователя {UserId}",
                        resetPasswordInfo.UserId
                    );
                    throw new NotValidOtpCodeException();
                }

                _metrics.Increment("otp_email_verified");
                _logger.LogDebug("Email OTP код успешно проверен для пользователя {UserId}", resetPasswordInfo.UserId);
            }

            if (string.IsNullOrEmpty(request.NewPassword))
            {
                throw new NewPasswordRequiredException();
            }

            // Новый пароль не должен совпадать с текущим. Проверяем ДО захвата reset —
            // иначе пользователь не сможет повторить с тем же кодом и другим паролем.
            var currentHash = await _passwordsStorage.GetUserPasswordHash(resetPasswordInfo.UserId, cancellationToken);
            if (PasswordHasher.VerifyPassword(request.NewPassword, currentHash))
            {
                _metrics.Increment("password_reset_confirmation_failed");
                _metrics.Increment("password_reset_confirmation_failed_same_as_old");
                throw new NewPasswordSameAsOldException();
            }

            var newPasswordHash = PasswordHasher.HashPassword(request.NewPassword);
            var deviceId = string.IsNullOrEmpty(requestContext.DeviceId)
                ? Guid.NewGuid().ToString()
                : requestContext.DeviceId;
            var refreshTokenString = RefreshTokenGenerator.GenerateRefreshToken();
            CreateTokenResponse accessTokenResponse;
            var notificationEnqueued = false;

            try
            {
                await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
                try
                {
                    // Захват reset, изменения сессий/пароля и уведомление фиксируются одним коммитом.
                    if (!await _resetPasswordsStorage.TryApprove(request.ResetId, cancellationToken))
                    {
                        _metrics.Increment("password_reset_confirmation_failed");
                        _metrics.Increment("password_reset_confirmation_failed_already_used");
                        _logger.LogWarning(
                            "Reset ID {ResetId} уже был использован для пользователя {UserId}",
                            request.ResetId,
                            resetPasswordInfo.UserId
                        );
                        throw new ResetIdHasIsApprovedException();
                    }

                    if (request.RevokeOtherSessions)
                    {
                        _logger.LogDebug("Отзыв прежних сессий пользователя {UserId}", resetPasswordInfo.UserId);
                        await refreshTokensStorage.RevokeAllSessions(resetPasswordInfo.UserId, deviceId, cancellationToken);
                    }

                    _logger.LogDebug("Установка нового хеша пароля для пользователя {UserId}", resetPasswordInfo.UserId);
                    await _passwordsStorage.UpdateUserPasswordHash(resetPasswordInfo.UserId, newPasswordHash, cancellationToken);

                    _logger.LogDebug("Генерация refresh token для пользователя {UserId}", resetPasswordInfo.UserId);
                    await refreshTokensStorage.CreateNewRefreshToken(refreshTokenString, resetPasswordInfo.UserId, deviceId,
                        ExpDaysRefreshToken, cancellationToken);
                    accessTokenResponse = await _mediator.Send(new CreateTokenCommand
                    {
                        RefreshToken = refreshTokenString,
                        DeferSuccessTelemetry = true
                    }, cancellationToken);
                    notificationEnqueued = await _passwordChangedNotifier.NotifyAsync(resetPasswordInfo.UserId, cancellationToken);
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

            if (request.RevokeOtherSessions)
            {
                _metrics.Increment("sessions_revoked");
            }

            if (notificationEnqueued)
            {
                _metrics.Increment("notification_outbox_enqueued");
            }

            _metrics.Increment("password_resets_confirmed");
            _metrics.Increment("sessions_created");
            _metrics.Increment("tokens_refreshed");

            _logger.LogInformation(
                "Сброс пароля успешно подтвержден для пользователя {UserId}, устройство: {DeviceName}",
                resetPasswordInfo.UserId,
                requestContext.DeviceName
            );

            return new ConfirmResetPasswordResponse()
            {
                AccessToken = accessTokenResponse.AccessToken,
                RefreshToken = new Token
                {
                    ExpirationDate = Timestamp.FromDateTime(DateTime.UtcNow.AddDays(ExpDaysRefreshToken)),
                    Value = refreshTokenString
                }
            };
        }

        // Пустого сохранённого кода быть не должно, но «пусто == пусто» подтверждением считать нельзя.
        private static bool FixedTimeEquals(string? stored, string? provided)
            => !string.IsNullOrEmpty(stored)
               && CryptographicOperations.FixedTimeEquals(
                   Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(provided ?? string.Empty));
    }
}
