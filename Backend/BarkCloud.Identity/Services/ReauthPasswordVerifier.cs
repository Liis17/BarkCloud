using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Shared.Exceptions.Identity;

namespace BarkCloud.Identity.Services;

/// <summary>
/// Повторная аутентификация владельца паролем перед изменением 2FA. Каждая попытка занимается в хранилище
/// до проверки пароля, поэтому подбор через похищенную сессию ограничен лимитом окна (см. <see cref="IAuthPropertiesStorage.TryReserveReauthPasswordAttempt"/>).
/// </summary>
public class ReauthPasswordVerifier(IAuthPropertiesStorage authPropertiesStorage, IPasswordsStorage passwordsStorage)
{
    /// <exception cref="InvalidPasswordException">Пароль пуст, не совпал или у аккаунта нет хеша пароля.</exception>
    /// <exception cref="PasswordAttemptsExceededException">Исчерпан лимит попыток в текущем окне.</exception>
    public async Task VerifyAsync(long userId, string? password)
    {
        // Пустой пароль не расходует попытку и не запускает bcrypt: подобрать им ничего нельзя.
        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidPasswordException();
        }

        if (!await authPropertiesStorage.TryReserveReauthPasswordAttempt(userId))
        {
            throw new PasswordAttemptsExceededException();
        }

        var passwordHash = await passwordsStorage.GetUserPasswordHash(userId);

        if (!PasswordHasher.VerifyPassword(password, passwordHash))
        {
            throw new InvalidPasswordException();
        }

        await authPropertiesStorage.ResetReauthPasswordAttempts(userId);
    }
}
