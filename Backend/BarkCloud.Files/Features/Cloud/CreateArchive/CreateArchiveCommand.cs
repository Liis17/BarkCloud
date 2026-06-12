using BarkCloud.Proto.Files;

using MediatR;

namespace BarkCloud.Files.Features.Cloud.CreateArchive;

/// <summary>
/// Собрать выбранные файлы / папку / альбом в ZIP, положить готовый блоб сразу в корзину
/// (срок удаления 3 дня) и вернуть временную ссылку для немедленного скачивания.
/// Источники комбинируются: всё заданное попадает в один архив.
/// </summary>
public class CreateArchiveCommand : IRequest<CreateArchiveResponse>
{
    /// <summary>Записи иерархии (выделение во вкладке «Файлы»).</summary>
    public List<Guid> EntryIds { get; set; } = new();

    /// <summary>Блобы напрямую (выделение в галерее Фото/Видео).</summary>
    public List<Guid> FileIds { get; set; } = new();

    /// <summary>Вся папка рекурсивно (null — не задано).</summary>
    public Guid? DirectoryId { get; set; }

    /// <summary>Весь альбом (null — не задано).</summary>
    public Guid? AlbumId { get; set; }

    /// <summary>Желаемое имя архива без расширения (необяз.).</summary>
    public string? ArchiveName { get; set; }
}
