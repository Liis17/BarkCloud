namespace BarkCloud.Shared.Identity;

public class IdentityClaims
{
    public const string UserId = "x-user-id";

    public const string TokenType = "x-token-type";

    public const string ServiceId = "x-service-id";

    public const string DeviceId = "x-device-id";

    /// <summary>Идентификатор сессии (Id refresh-токена) в access-токене пользователя.</summary>
    public const string SessionId = "x-session-id";

    /// <summary>Сервис-адресат сервисного токена: проверяется на совпадение с самим сервисом.</summary>
    public const string TargetService = "x-target-service";

    /// <summary>Разрешения сервисного токена, пробел-разделённые (см. <see cref="ServicePermissions"/>).</summary>
    public const string Permissions = "x-permissions";
}