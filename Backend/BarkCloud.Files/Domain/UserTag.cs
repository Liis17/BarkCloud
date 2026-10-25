namespace BarkCloud.Files.Domain;

/// <summary>Личный каталог поисковых тегов; назначения файлам остаются совместимыми со строковым API.</summary>
public class UserTag
{
    public Guid Id { get; set; }
    public long OwnerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string Color { get; set; } = "#6750A4";
}
