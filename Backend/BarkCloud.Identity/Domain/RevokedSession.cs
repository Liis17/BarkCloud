namespace BarkCloud.Identity.Domain;

public class RevokedSession
{
    public long Id { get; set; }

    public long UserId { get; set; }

    public string DeviceId { get; set; } = null!;

    public DateTime RevokedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    /// <summary>Задано — отзыв по сессии (у устройства были refresh, и они удалены): отклоняются токены с sid не больше
    /// значения. Не задано — устройство без refresh, отзыв по времени (<see cref="RevokedAt"/>).</summary>
    public long? MaxSessionId { get; set; }
}
