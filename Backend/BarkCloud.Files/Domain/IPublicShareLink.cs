namespace BarkCloud.Files.Domain;

/// <summary>Общие поля публичной ссылки, которые меняются при повторной публикации объекта.</summary>
public interface IPublicShareLink
{
    string Token { get; set; }

    DateTime CreatedAt { get; set; }

    long ClickCount { get; set; }

    /// <summary>Когда ссылка перестаёт открываться (UTC); <c>null</c> — бессрочно.</summary>
    DateTime? ExpiresAt { get; set; }
}
