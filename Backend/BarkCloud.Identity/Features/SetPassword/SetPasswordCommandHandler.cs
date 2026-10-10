
namespace BarkCloud.Identity.Features.SetPassword;

using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Identity.Persistence.Contexts;

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
    private readonly IdentityContext _context;

    public SetPasswordCommandHandler(UserContext userContext, IPasswordsStorage passwordsStorage,
        IAuthPropertiesStorage authPropertiesStorage, IRefreshTokensStorage refreshTokensStorage, PasswordChangedNotifier passwordChangedNotifier,
        MetricsCollector metrics, ILogger<SetPasswordCommandHandler> logger, IdentityContext context)
    {
        _userContext = userContext;
        _passwordsStorage = passwordsStorage;
        _authPropertiesStorage = authPropertiesStorage;
        this.refreshTokensStorage = refreshTokensStorage;
        _passwordChangedNotifier = passwordChangedNotifier;
        _metrics = metrics;
        _logger = logger;
        _context = context;
    }

    public async Task Handle(SetPasswordCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Начало изменения пароля для пользователя {UserId}",
            _userContext.UserId
        );

        var currentHash = await _passwordsStorage.GetUserPasswordHash(_userContext.UserId, cancellationToken);
        if (currentHash != null)
        {
            if (string.IsNullOrEmpty(request.OldPassword))
            {
                _metrics.Increment("password_change_failed_invalid_old");
                throw new InvalidOldPasswordException();
            }

            // Старый пароль под токеном — тот же счётчик, что у повторной аутентификации 2FA: попытка занимается до bcrypt.
            if (!await _authPropertiesStorage.TryReserveReauthPasswordAttempt(_userContext.UserId, cancellationToken))
            {
                _metrics.Increment("password_change_failed_attempts_exceeded");
                throw new PasswordAttemptsExceededException();
            }

            if (!PasswordHasher.VerifyPassword(request.OldPassword, currentHash))
            {
                _metrics.Increment("password_change_failed_invalid_old");
                throw new InvalidOldPasswordException();
            }

            await _authPropertiesStorage.ResetReauthPasswordAttempts(_userContext.UserId, cancellationToken);

            // Старый пароль уже проверен, поэтому достаточно сравнить строки — второй bcrypt не нужен.
            if (string.Equals(request.NewPassword, request.OldPassword, StringComparison.Ordinal))
            {
                _metrics.Increment("password_change_failed_same_as_old");
                throw new NewPasswordSameAsOldException();
            }
        }

        var passwordHash = PasswordHasher.HashPassword(request.NewPassword);

        _logger.LogDebug("Обновление хэша пароля в БД для пользователя {UserId}", _userContext.UserId);

        bool isNewUser;
        var notificationEnqueued = false;
        try
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                isNewUser = await _passwordsStorage.UpdateUserPasswordHash(_userContext.UserId, passwordHash, cancellationToken);

                if (!isNewUser)
                {
                    notificationEnqueued = await _passwordChangedNotifier.NotifyAsync(_userContext.UserId, cancellationToken);
                }

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

        _metrics.Increment("password_changes");
        if (isNewUser)
        {
            _metrics.Increment("password_changes_initial");
        }
        if (notificationEnqueued)
        {
            _metrics.Increment("notification_outbox_enqueued");
        }

        _logger.LogInformation(
            "Пароль успешно изменен для пользователя {UserId}",
            _userContext.UserId
        );
    }
}
