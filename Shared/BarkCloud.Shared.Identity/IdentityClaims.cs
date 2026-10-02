namespace BarkCloud.Shared.Identity;

public class IdentityClaims
{
    public const string UserId = "x-user-id";

    public const string TokenType = "x-token-type";

    public const string ServiceId = "x-service-id";

    public const string DeviceId = "x-device-id";

    /// <summary>Идентификатор сессии (Id refresh-токена) в access-токене пользователя.</summary>
    public const string SessionId = "x-session-id";
}