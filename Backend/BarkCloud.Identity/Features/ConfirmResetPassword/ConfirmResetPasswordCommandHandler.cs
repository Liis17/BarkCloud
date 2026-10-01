using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Settings;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Queue.Identity;

using MassTransit;

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
        private readonly IPublishEndpoint _publishEndpoint;
        private readonly JwtSettings _jwtSettings;
        private readonly PasswordChangedNotifier _passwordChangedNotifier;
        private readonly RequestContext requestContext;
        private readonly MetricsCollector _metrics;
        private readonly ILogger<ConfirmResetPasswordCommandHandler> _logger;

        private const int ExpDaysRefreshToken = 9999;


        public ConfirmResetPasswordCommandHandler(IResetPasswordsStorage resetPasswordsStorage, IAuthPropertiesStorage authPropertiesStorage,
            IPasswordsStorage passwordsStorage, IRefreshTokensStorage refreshTokensStorage, IMediator mediator,
            IPublishEndpoint publishEndpoint, JwtSettings jwtSettings, PasswordChangedNotifier passwordChangedNotifier,
            RequestContext requestContext, MetricsCollector metrics, ILogger<ConfirmResetPasswordCommandHandler> logger)
        {
            _resetPasswordsStorage = resetPasswordsStorage;
            _authPropertiesStorage = authPropertiesStorage;
            _passwordsStorage = passwordsStorage;
            this.refreshTokensStorage = refreshTokensStorage;
            _mediator = mediator;
            _publishEndpoint = publishEndpoint;
            _jwtSettings = jwtSettings;
            _passwordChangedNotifier = passwordChangedNotifier;
            this.requestContext = requestContext;
            _metrics = metrics;
            _logger = logger;
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

            var resetPasswordInfo = await _resetPasswordsStorage.GetResetPassword(request.ResetId);

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

            if (resetPasswordInfo.OtpType == OtpType.Authenticator)
            {
                var otpSecret = await _authPropertiesStorage.GetOtpSecretKey(resetPasswordInfo.UserId);

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
                if (!string.Equals(resetPasswordInfo.OtpCode, request.OtpCode, StringComparison.Ordinal))
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
            var currentHash = await _passwordsStorage.GetUserPasswordHash(resetPasswordInfo.UserId);
            if (PasswordHasher.VerifyPassword(request.NewPassword, currentHash))
            {
                _metrics.Increment("password_reset_confirmation_failed");
                _metrics.Increment("password_reset_confirmation_failed_same_as_old");
                throw new NewPasswordSameAsOldException();
            }

            // Атомарный захват reset: при параллельных подтверждениях успех получает только один.
            if (!await _resetPasswordsStorage.TryApprove(request.ResetId))
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

            var deviceId = string.IsNullOrEmpty(requestContext.DeviceId)
                ? Guid.NewGuid().ToString()
                : requestContext.DeviceId;

            // Отзыв прежних сессий — до записи нового хеша: при сбое между шагами пароль остаётся прежним.
            if (request.RevokeOtherSessions)
            {
                _logger.LogDebug("Отзыв прежних сессий пользователя {UserId}", resetPasswordInfo.UserId);

                var revokedDeviceIds = await refreshTokensStorage.DeleteAllByUserId(resetPasswordInfo.UserId);

                // Для текущего устройства событие не публикуем: оно придёт асинхронно и отозвало бы
                // только что выданный токен (iat <= RevokedAt), как при повторном входе в SessionIssuer.
                foreach (var revokedDeviceId in revokedDeviceIds.Where(x => x != deviceId))
                {
                    await _publishEndpoint.Publish(new SessionRevokedEvent
                    {
                        UserId = resetPasswordInfo.UserId,
                        DeviceId = revokedDeviceId,
                        AccessTokenExpiresAt = DateTime.UtcNow.AddMinutes(_jwtSettings.ExpiryMinutes)
                    }, cancellationToken);
                }

                _metrics.Increment("sessions_revoked");
            }

            _logger.LogDebug("Установка нового хеша пароля для пользователя {UserId}", resetPasswordInfo.UserId);
            await _passwordsStorage.UpdateUserPasswordHash(resetPasswordInfo.UserId, PasswordHasher.HashPassword(request.NewPassword));

            _logger.LogDebug("Генерация refresh token для пользователя {UserId}", resetPasswordInfo.UserId);

            var refreshTokenString = RefreshTokenGenerator.GenerateRefreshToken();
            await refreshTokensStorage.CreateNewRefreshToken(refreshTokenString, resetPasswordInfo.UserId, deviceId, ExpDaysRefreshToken);

            var accessTokenResponse = await _mediator.Send(new CreateTokenCommand { RefreshToken = refreshTokenString }, cancellationToken);

            try
            {
                await _passwordChangedNotifier.NotifyAsync(resetPasswordInfo.UserId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Не удалось отправить уведомление о смене пароля пользователю {UserId}", resetPasswordInfo.UserId);
            }

            _metrics.Increment("password_resets_confirmed");
            _metrics.Increment("sessions_created");

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
    }
}