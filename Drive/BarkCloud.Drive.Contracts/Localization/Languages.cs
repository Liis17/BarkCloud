namespace BarkCloud.Drive.Contracts.Localization;

// Поддерживаемые языки UI. Добавить язык = +1 запись здесь + файл Strings.<code>.resx.
public sealed record Language(string Code, string NativeName)
{
    public override string ToString() => NativeName;
}

public static class Languages
{
    public static readonly IReadOnlyList<Language> All =
    [
        new("ru", "Русский"),
        new("en", "English"),
        new("de", "Deutsch"),
    ];

    // Язык по умолчанию из культуры Windows: ru/de поддержаны напрямую, иначе English.
    public static string DefaultForSystem()
    {
        var two = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return All.Any(l => l.Code == two) ? two : "en";
    }
}
