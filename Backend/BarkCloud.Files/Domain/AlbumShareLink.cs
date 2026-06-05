using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Files.Domain;

/// <summary>
/// Постоянная публичная ссылка на альбом (<see cref="Album"/>). Создаётся владельцем,
/// резолвится по <see cref="Token"/> анонимно (через сервисный RPC) в динамическую страницу:
/// элементы альбома (фото/видео) отдаются всегда актуальными. Один публичный шар на альбом
/// (уникальность по владельцу+альбому).
/// </summary>
public class AlbumShareLink
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>Владелец альбома (создатель).</summary>
    public long OwnerId { get; set; }

    /// <summary>Идентификатор расшаренного <see cref="Album"/>.</summary>
    public Guid AlbumId { get; set; }

    /// <summary>URL-safe токен (base64url от 16 случайных байт). Уникален.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Имя альбома на момент публикации (для отображения в списке).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Когда опубликован (для сортировки списка).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Сколько раз открывали публичный альбом.</summary>
    public long ClickCount { get; set; }
}
