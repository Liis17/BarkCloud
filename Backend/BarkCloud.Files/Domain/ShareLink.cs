using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Files.Domain;

/// <summary>
/// Постоянная публичная ссылка на файл (<see cref="UploadFile"/>). Создаётся владельцем,
/// резолвится по <see cref="Token"/> анонимно (через сервисный RPC) в публичный URL скачивания.
/// </summary>
public class ShareLink
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>Владелец ссылки (создатель).</summary>
    public long OwnerId { get; set; }

    /// <summary>Идентификатор реального <see cref="UploadFile"/>.</summary>
    public Guid FileId { get; set; }

    /// <summary>URL-safe токен (base64url от 16 случайных байт). Уникален.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Имя файла на момент создания ссылки (для отображения в списке).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Когда создана ссылка (для сортировки списка).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Сколько раз по ссылке переходили.</summary>
    public long ClickCount { get; set; }
}
