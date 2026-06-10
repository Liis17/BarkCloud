namespace BarkCloud.Files.Domain;

/// <summary>
/// Реестр виртуальных системных умных папок. Они не хранятся в БД, а отдаются в списке
/// вместе с пользовательскими (см. ListDynamicFolders) с well-known строковыми id.
/// Критерии захардкожены здесь, поэтому определение меняется централизованно и не требует
/// миграции-сидинга на каждого пользователя.
/// </summary>
public static class SystemDynamicFolders
{
    public const string KeyRecentMedia = "sys-recent-media";
    public const string KeyRecentDocs = "sys-recent-docs";
    public const string KeyLarge = "sys-large";
    public const string KeyScreenshots = "sys-screenshots";
    public const string KeyDuplicateMedia = "sys-duplicate-media";
    public const string KeyDuplicateFiles = "sys-duplicate-files";

    /// <summary>«Недавно загруженные» — за последние N дней.</summary>
    public const int RecentDays = 3;

    /// <summary>Порог «больших файлов» — 100 МБ в байтах.</summary>
    public const long LargeSizeBytes = 100L * 1024 * 1024;

    /// <summary>Подстрока имени для «Скриншотов» (поиск регистронезависимый).</summary>
    public const string ScreenshotToken = "screenshot";

    /// <summary>
    /// Все системные папки в фиксированном порядке. <see cref="DynamicFolder.Id"/> не используется
    /// (системные адресуются по <see cref="DynamicFolder.SystemKey"/>).
    /// </summary>
    public static IReadOnlyList<DynamicFolder> All()
    {
        var recentDays = new DynamicFolderRule { Field = DfField.Date, Operator = DfOperator.WithinLastDays, Value = RecentDays.ToString() };
        var mediaKinds = new DynamicFolderRule
        {
            Field = DfField.MediaKind,
            Operator = DfOperator.Equals,
            Value = $"{(int)MediaKind.Photo},{(int)MediaKind.Video}"
        };
        var docKind = new DynamicFolderRule { Field = DfField.MediaKind, Operator = DfOperator.Equals, Value = ((int)MediaKind.Document).ToString() };

        return new[]
        {
            Build(KeyRecentMedia, "Недавние фото и видео", "clock", "#4F9DDE", 0, DfViewMode.Grid,
                recentDays, mediaKinds),

            Build(KeyRecentDocs, "Недавние документы", "doc", "#5C97A8", 1, DfViewMode.List,
                recentDays, docKind),

            Build(KeyLarge, "Большие файлы", "hdd", "#E0883B", 2, DfViewMode.Grid,
                new DynamicFolderRule { Field = DfField.Size, Operator = DfOperator.GreaterThan, Value = LargeSizeBytes.ToString() }),

            Build(KeyScreenshots, "Скриншоты", "camera", "#7E57C2", 3, DfViewMode.Grid,
                new DynamicFolderRule { Field = DfField.Name, Operator = DfOperator.Contains, Value = ScreenshotToken }),

            Build(KeyDuplicateMedia, "Дубликаты фото и видео", "photo", "#2F8F83", 4, DfViewMode.Grid),

            Build(KeyDuplicateFiles, "Дубликаты файлов", "doc", "#8A6BBE", 5, DfViewMode.List),
        };
    }

    /// <summary>Критерии системной папки по её ключу; null — ключ не системный.</summary>
    public static DynamicFolderCriteria? CriteriaFor(string systemKey)
    {
        foreach (var folder in All())
            if (folder.SystemKey == systemKey)
                return folder.Criteria;
        return null;
    }

    public static bool IsSystemKey(string? id) => id is not null && id.StartsWith("sys-", StringComparison.Ordinal);

    public static bool IsDuplicateKey(string? id) => id is KeyDuplicateMedia or KeyDuplicateFiles;

    public static bool IsDuplicateMediaKey(string? id) => id == KeyDuplicateMedia;

    private static DynamicFolder Build(string key, string name, string icon, string color, int order, DfViewMode viewMode, params DynamicFolderRule[] rules)
    {
        return new DynamicFolder
        {
            Id = Guid.Empty,
            OwnerId = 0,
            Name = name,
            IsSystem = true,
            SystemKey = key,
            IconKey = icon,
            CoverColor = color,
            ViewMode = viewMode,
            SortOrder = order,
            Criteria = new DynamicFolderCriteria
            {
                Combinator = DfCombinator.All,
                Rules = rules.ToList()
            },
            CreatedAt = DateTime.UnixEpoch,
            UpdatedAt = DateTime.UnixEpoch
        };
    }
}
