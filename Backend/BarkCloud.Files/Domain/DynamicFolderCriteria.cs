namespace BarkCloud.Files.Domain;

/// <summary>
/// Набор критериев умной папки. Сериализуется целиком в jsonb-колонку
/// (см. конфигурацию <see cref="DynamicFolder"/> в FilesContext). Пустой набор правил = «все файлы».
/// </summary>
public class DynamicFolderCriteria
{
    public DfCombinator Combinator { get; set; } = DfCombinator.All;

    public List<DynamicFolderRule> Rules { get; set; } = new();
}
