namespace BarkCloud.Shared.Identity;

/// <summary>
/// Разрешения межсервисных server-API. Выдаются только Identity при выпуске сервисного токена
/// (клейм <c>x-permissions</c>) и не выбираются клиентом. Минимальные права по текущим вызовам:
/// Torrent, например, получает только чтение отзыва сессий (аудит безопасности, п. 9).
/// </summary>
public static class ServicePermissions
{
    /// <summary>Чтение ленты отзывов сессий (SessionRevocationApi).</summary>
    public const string RevocationRead = "revocation.read";

    /// <summary>Чтение UsersServerApi: FindByLogin, GetById, GetUserContacts, ListByIds, GetUserDevices.</summary>
    public const string UsersServerRead = "users-server.read";

    /// <summary>Изменение UsersServerApi: черновики, ConfirmUser, устройства, аватар.</summary>
    public const string UsersServerWrite = "users-server.write";

    /// <summary>Чтение FilesServerApi: GetFileData, Resolve*Share.</summary>
    public const string FilesServerRead = "files-server.read";

    /// <summary>Изменение FilesServerApi: UploadAvatarServer.</summary>
    public const string FilesServerWrite = "files-server.write";

    /// <summary>Управление S3-миграцией (StorageCutover RPC).</summary>
    public const string FilesServerStorageAdmin = "files-server.storage-admin";

    public static readonly IReadOnlyList<string> All =
    [
        RevocationRead,
        UsersServerRead,
        UsersServerWrite,
        FilesServerRead,
        FilesServerWrite,
        FilesServerStorageAdmin
    ];

    /// <summary>Разрешения сервиса-вызывающего; пустой набор — сервис не ходит по server-API.</summary>
    public static IReadOnlySet<string> For(ServiceId serviceId)
    {
        var permissions = serviceId switch
        {
            ServiceId.Identity => [UsersServerRead, UsersServerWrite],
            ServiceId.Users => [RevocationRead, FilesServerRead],
            ServiceId.Files => [RevocationRead, UsersServerRead],
            ServiceId.Torrent => [RevocationRead],
            // Web — доверенный административный компонент (обслуживание, аватары, публичные страницы).
            ServiceId.Web =>
            [
                RevocationRead,
                UsersServerRead, UsersServerWrite,
                FilesServerRead, FilesServerWrite, FilesServerStorageAdmin
            ],
            _ => Array.Empty<string>()
        };

        return new HashSet<string>(permissions, StringComparer.Ordinal);
    }
}
