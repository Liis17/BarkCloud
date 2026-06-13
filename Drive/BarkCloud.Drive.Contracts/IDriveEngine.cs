namespace BarkCloud.Drive.Contracts;

// Контракт IPC между UI (App) и движком (Engine).
// Реализуется в Engine, вызывается из App через StreamJsonRpc поверх named pipe.
public interface IDriveEngine
{
    // Логин логином/паролем (+ опциональный OTP). Движок сам хранит и обновляет токен.
    Task<EngineStatus> LoginAsync(string login, string password, string? otpCode);

    // Начать passwordless-вход по ключу (WebAuthn): вернуть challenge/options и RP ID
    // для системного вызова в UI. Логин не нужен (discoverable). Пустой ChallengeId — недоступно.
    Task<WebAuthnChallenge> BeginWebAuthnAsync();

    // Завершить вход по ключу: проверить assertion на сервере и сохранить сессию.
    Task<EngineStatus> CompleteWebAuthnAsync(string challengeId, string assertionJson);

    // Выйти из аккаунта: отмонтировать диск, стереть refresh-токен, обнулить сессию.
    Task<EngineStatus> LogoutAsync();

    // Примонтировать диск на указанную букву (например "X") с меткой тома (имя диска).
    Task<EngineStatus> MountAsync(string driveLetter, string? volumeLabel);

    // Перемонтировать с новой буквой и/или меткой (null = оставить текущее).
    // Метка тома и точка монтирования читаются Dokany только при маунте — поэтому
    // переименование/смена буквы делаются через переподключение диска.
    Task<EngineStatus> RemountAsync(string? driveLetter, string? volumeLabel);

    // Отмонтировать диск (движок остаётся жив).
    Task<EngineStatus> UnmountAsync();

    // Текущее состояние (для опроса из UI).
    Task<EngineStatus> GetStatusAsync();

    // Байты аватара текущего пользователя (PNG/JPG) или null, если аватара нет.
    Task<byte[]?> GetAvatarAsync();

    // Текущие настройки движка (папка кэша и т.п.).
    Task<EngineSettings> GetSettingsAsync();

    // Сменить папку кэша. Применяется к новым скачиваниям; уже скачанное остаётся
    // в прежней папке. Возвращает применённые настройки.
    Task<EngineSettings> SetCacheDirAsync(string path);

    // Язык для строк статуса/ошибок, которые движок кладёт в EngineStatus
    // (применяется к последующим сообщениям). Код культуры: "ru"/"en"/"de".
    Task SetLanguageAsync(string culture);

    // Отмонтировать и завершить процесс движка.
    Task ShutdownAsync();
}
