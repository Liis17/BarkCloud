namespace BarkCloud.Files.Domain;

/// <summary>
/// Одно правило умной папки. Хранится внутри JSON-документа <see cref="DynamicFolderCriteria"/>,
/// поэтому это обычный POCO без ключа. Значение унифицировано строкой и парсится по типу поля.
/// </summary>
public class DynamicFolderRule
{
    public DfField Field { get; set; }

    public DfOperator Operator { get; set; }

    public string Value { get; set; } = "";
}
