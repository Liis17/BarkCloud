
namespace BarkCloud.Identity.Features.SetPassword;

using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Shared.Exceptions.Identity;

using GrpcServer.XAuth;

using MediatR;

using Microsoft.Extensions.Logging;

using Persistence.Services;

using Services;

public class SetPasswordCommandHandler : IRequestHandler<SetPasswordCommand>
{
    private readonly UserContext _userContext;
    private readonly IPasswordsStorage _passwordsStorage;
    private readonly IAuthPropertiesStorage _authPropertiesStorage;
    private readonly IRefreshTokensStorage refreshTokensStorage;
    private readonly PasswordChangedNotifier _passwordChangedNotifier;
    private readonly MetricsCollector _metrics;
    private readonly ILogger<SetPasswordCommandHandler> _logger;

    public SetPasswordCommandHandler(UserContext userContext, IPasswordsStorage passwordsStorage,
        IAuthPropertiesStorage authPropertiesStorage, IRefreshTokensStorage refreshTokensStorage, PasswordChangedNotifier passwordChangedNotifier,
        MetricsCollector metrics, ILogger<SetPasswordCommandHandler> logger)
    {
        _userContext = userContext;
        _passwordsStorage = passwordsStorage;
        _authPropertiesStorage = authPropertiesStorage;
        this.refreshTokensStorage = refreshTokensStorage;
        _passwordChangedNotifier = passwordChangedNotifier;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task Handle(SetPasswordCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Начало изменения пароля для пользователя {UserId}",
            _userContext.UserId
        );

        var currentHash = await _passwordsStorage.GetUserPasswordHash(_userContext.UserId);
        if (currentHash != null)
        {
            if (string.IsNullOrEmpty(request.OldPassword))
            {
                _metrics.Increment("password_change_failed_invalid_old");
                throw new InvalidOldPasswordException();
            }

            // Старый пароль под токеном — тот же счётчик, что у повторной аутентификации 2FA: попытка занимается до bcrypt.
            if (!await _authPropertiesStorage.TryReserveReauthPasswordAttempt(_userContext.UserId))
            {
                _metrics.Increment("password_change_failed_attempts_exceeded");
                throw new PasswordAttemptsExceededException();
            }

            if (!PasswordHasher.VerifyPassword(request.OldPassword, currentHash))
            {
                _metrics.Increment("password_change_failed_invalid_old");
                throw new InvalidOldPasswordException();
            }

            await _authPropertiesStorage.ResetReauthPasswordAttempts(_userContext.UserId);

            // Старый пароль уже проверен, поэтому достаточно сравнить строки — второй bcrypt не нужен.
            if (string.Equals(request.NewPassword, request.OldPassword, StringComparison.Ordinal))
            {
                _metrics.Increment("password_change_failed_same_as_old");
                throw new NewPasswordSameAsOldException();
            }
        }

        var passwordHash = PasswordHasher.HashPassword(request.NewPassword);

        _logger.LogDebug("Обновление хэша пароля в БД для пользователя {UserId}", _userContext.UserId);

        var isNewUser = await _passwordsStorage.UpdateUserPasswordHash(_userContext.UserId, passwordHash);

        _metrics.Increment("password_changes");
        if (isNewUser)
        {
            _metrics.Increment("password_changes_initial");
        }

        if (!isNewUser)
        {
            await _passwordChangedNotifier.NotifyAsync(_userContext.UserId);
        }

        _logger.LogInformation(
            "Пароль успешно изменен для пользователя {UserId}. Уведомление поставлено в очередь",
            _userContext.UserId
        );
    }
}
