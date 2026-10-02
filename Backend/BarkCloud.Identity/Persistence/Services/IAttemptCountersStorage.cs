namespace BarkCloud.Identity.Persistence.Services;

/// <param name="Allowed">Попытка занята; false — лимит окна исчерпан.</param>
/// <param name="RetryAfter">До конца текущего окна (только при отказе).</param>
public readonly record struct AttemptReservation(bool Allowed, TimeSpan RetryAfter);

public interface IAttemptCountersStorage
{
    Task<AttemptReservation> TryReserve(string key, int maxAttempts, TimeSpan window);
    Task Reset(string key);
}
