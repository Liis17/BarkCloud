namespace BarkCloud.Drive.Contracts;

// Контракт IPC между UI (App) и движком (Engine).
// Реализуется в Engine, вызывается из App через StreamJsonRpc поверх named pipe.
public interface IDriveEngine
{
    // Логин логином/паролем (+ опциональный OTP). Движок сам хранит и обновляет токен.
    Task<EngineStatus> LoginAsync(string login, string password, string? otpCode);

    // Примонтировать диск на указанную букву (например "X").
    Task<EngineStatus> MountAsync(string driveLetter);

    // Отмонтировать диск (движок остаётся жив).
    Task<EngineStatus> UnmountAsync();

    // Текущее состояние (для опроса из UI).
    Task<EngineStatus> GetStatusAsync();

    // Текущие настройки движка (папка кэша и т.п.).
    Task<EngineSettings> GetSettingsAsync();

    // Сменить папку кэша. Применяется к новым скачиваниям; уже скачанное остаётся
    // в прежней папке. Возвращает применённые настройки.
    Task<EngineSettings> SetCacheDirAsync(string path);

    // Отмонтировать и завершить процесс движка.
    Task ShutdownAsync();
}
