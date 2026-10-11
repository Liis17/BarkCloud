using BarkCloud.Proto.Users;

namespace BarkCloud.Drive.Engine;

// Имя текущего пользователя (для дашборда). UsersApi.GetUser(user_id=0) = «я».
// Кэшируется до Logout. Почту клиентский API не отдаёт — показываем только имя.
internal sealed class UserProfile(UsersApi.UsersApiClient users, Action<string>? ownerLoaded = null)
{
    private readonly object _lock = new();
    private readonly object _ownerCallbackLock = new();
    private long? _userId;
    private string? _username;
    private string? _avatarUrl;
    private long _epoch;

    public string? Username
    {
        get { lock (_lock) return _username; }
    }

    public string? UserId
    {
        get { lock (_lock) return _userId?.ToString(System.Globalization.CultureInfo.InvariantCulture); }
    }

    // URL аватара (оригинал или, если его нет, превью). Готовый /download-URL — нормализуется при скачивании.
    public string? AvatarUrl
    {
        get { lock (_lock) return _avatarUrl; }
    }

    public long Epoch
    {
        get { lock (_lock) return _epoch; }
    }

    // Ленивая загрузка имени (первый GetStatus после авторизации). Ошибка не критична.
    public async Task EnsureLoadedAsync()
    {
        long epoch;
        lock (_lock)
        {
            if (_userId != null)
                return;
            epoch = _epoch;
        }

        try
        {
            var user = (await users.GetUserAsync(new GetUserRequest { UserId = 0 })).User;
            lock (_lock)
            {
                if (epoch != _epoch)
                    return;
                _userId = user.Id;
                _username = user.Username;
                _avatarUrl = !string.IsNullOrEmpty(user.ProfilePicture)
                    ? user.ProfilePicture
                    : user.ProfilePicturePreview;
            }

            lock (_ownerCallbackLock)
            {
                lock (_lock)
                    if (epoch != _epoch)
                        return;
                // Сериализуем запись владельца с Clear(); состояние профиля при callback не заблокировано.
                ownerLoaded?.Invoke(user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        catch (Exception ex)
        {
            EngineLog.Error("UserProfile", ex);
        }
    }

    public void Clear(Action? clearOwner = null)
    {
        lock (_ownerCallbackLock)
        {
            lock (_lock)
            {
                ++_epoch;
                _userId = null;
                _username = null;
                _avatarUrl = null;
            }
            clearOwner?.Invoke();
        }
    }
}
