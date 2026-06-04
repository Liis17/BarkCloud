namespace BarkCloud.Files.Domain;

/// <summary>
/// Реестр виртуальных системных умных папок. Они не хранятся в БД, а отдаются в списке
/// вместе с пользовательскими (см. ListDynamicFolders) с well-known строковыми id.
/// Критерии захардкожены здесь, поэтому определение меняется централизованно и не требует
/// миграции-сидинга на каждого пользователя.
/// </summary>
public static class SystemDynamicFolders
{
    public const string KeyRecent = "sys-recent";
    public const string KeyLarge = "sys-large";
    public const string KeyScreenshots = "sys-screenshots";

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
        return new[]
        {
            Build(KeyRecent, "Недавно загруженные", "clock", "#4F9DDE", 0,
                new DynamicFolderRule { Field = DfField.Date, Operator = DfOperator.WithinLastDays, Value = RecentDays.ToString() }),

            Build(KeyLarge, "Большие файлы", "hdd", "#E0883B", 1,
                new DynamicFolderRule { Field = DfField.Size, Operator = DfOperator.GreaterThan, Value = LargeSizeBytes.ToString() }),

            Build(KeyScreenshots, "Скриншоты", "camera", "#7E57C2", 2,
                new DynamicFolderRule { Field = DfField.Name, Operator = DfOperator.Contains, Value = ScreenshotToken }),
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

    private static DynamicFolder Build(string key, string name, string icon, string color, int order, DynamicFolderRule rule)
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
            SortOrder = order,
            Criteria = new DynamicFolderCriteria
            {
                Combinator = DfCombinator.All,
                Rules = new List<DynamicFolderRule> { rule }
            },
            CreatedAt = DateTime.UnixEpoch,
            UpdatedAt = DateTime.UnixEpoch
        };
    }
}
