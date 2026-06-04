using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Files.Domain;

/// <summary>
/// Умная папка — виртуальная коллекция файлов пользователя, собираемая автоматически по критериям
/// (<see cref="DynamicFolderCriteria"/>). Содержимое не материализуется: вычисляется на лету
/// (см. DynamicFolderQueryBuilder). Один файл может попадать в несколько папок.
/// Системные папки («Недавние фото и видео», «Недавние документы», «Большие файлы», «Скриншоты») в БД не хранятся —
/// отдаются виртуально (см. <see cref="SystemDynamicFolders"/>); поля <see cref="IsSystem"/>/<see cref="SystemKey"/>
/// заполняются только для них в памяти.
/// </summary>
public class DynamicFolder
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>Владелец папки.</summary>
    public long OwnerId { get; set; }

    public string Name { get; set; } = "";

    /// <summary>true только для виртуальных системных папок (в БД всегда false).</summary>
    public bool IsSystem { get; set; }

    /// <summary>Well-known ключ системной папки (sys-recent-media / sys-recent-docs / sys-large / sys-screenshots). null для пользовательских.</summary>
    public string? SystemKey { get; set; }

    /// <summary>Критерии сбора. Хранится как jsonb.</summary>
    public DynamicFolderCriteria Criteria { get; set; } = new();

    /// <summary>Подсказка иконки для UI (clock / hdd / camera / …).</summary>
    public string? IconKey { get; set; }

    /// <summary>Hex-цвет плитки, если нет обложки-файла.</summary>
    public string? CoverColor { get; set; }

    /// <summary>Как отображать содержимое в просмотре: сетка превью или список.</summary>
    public DfViewMode ViewMode { get; set; } = DfViewMode.Grid;

    /// <summary>Порядок в ленте (пользовательские — после системных).</summary>
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
