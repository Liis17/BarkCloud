namespace BarkCloud.Identity.Domain;

/// <summary>
/// Счётчик попыток/отправок в окне для ограничений без собственной строки-владельца (источник запроса, аккаунт, получатель письма).
/// Окно открывается первой попыткой; после <see cref="WindowEndsAt"/> следующая попытка начинает новое.
/// </summary>
public class AuthAttemptCounter
{
    public string Key { get; set; } = null!;

    public int Count { get; set; }

    public DateTime WindowEndsAt { get; set; }
}
