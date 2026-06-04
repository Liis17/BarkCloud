namespace BarkCloud.Files.Domain;

/// <summary>
/// Режим отображения содержимого умной папки. Совпадает с proto-enum <c>DfViewMode</c>.
/// </summary>
public enum DfViewMode
{
    Grid = 0, // сетка превью (удобно для фото/видео)
    List = 1  // список строк (удобно для документов/аудио)
}
