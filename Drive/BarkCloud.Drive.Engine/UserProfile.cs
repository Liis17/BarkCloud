using BarkCloud.Proto.Users;

namespace BarkCloud.Drive.Engine;

// Имя текущего пользователя (для дашборда). UsersApi.GetUser(user_id=0) = «я».
// Кэшируется до Logout. Почту клиентский API не отдаёт — показываем только имя.
internal sealed class UserProfile(UsersApi.UsersApiClient users)
{
    private readonly object _lock = new();
    private string? _username;

    public string? Username
    {
        get { lock (_lock) return _username; }
    }

    // Ленивая загрузка имени (первый GetStatus после авторизации). Ошибка не критична.
    public async Task EnsureLoadedAsync()
    {
        if (Username != null)
            return;

        try
        {
            var resp = await users.GetUserAsync(new GetUserRequest { UserId = 0 });
            lock (_lock) _username = resp.User.Username;
        }
        catch (Exception ex)
        {
            EngineLog.Error("UserProfile", ex);
        }
    }

    public void Clear()
    {
        lock (_lock) _username = null;
    }
}
