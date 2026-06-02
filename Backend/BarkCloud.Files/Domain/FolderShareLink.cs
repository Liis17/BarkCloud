using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Files.Domain;

/// <summary>
/// Постоянная публичная ссылка на папку (<see cref="CloudDirectory"/>). Создаётся владельцем,
/// резолвится по <see cref="Token"/> анонимно (через сервисный RPC) в динамическую страницу:
/// содержимое папки (подпапки + файлы) отдаётся всегда актуальным, отдельные публичные ссылки
/// на каждый файл не создаются. Один публичный шар на папку (уникальность по владельцу+папке).
/// </summary>
public class FolderShareLink
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>Владелец папки (создатель).</summary>
    public long OwnerId { get; set; }

    /// <summary>Идентификатор расшаренной <see cref="CloudDirectory"/> (корень публичного поддерева).</summary>
    public Guid DirectoryId { get; set; }

    /// <summary>URL-safe токен (base64url от 16 случайных байт). Уникален.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Имя папки на момент публикации (для отображения в списке).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Когда опубликована (для сортировки списка).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Сколько раз открывали публичную папку.</summary>
    public long ClickCount { get; set; }
}
