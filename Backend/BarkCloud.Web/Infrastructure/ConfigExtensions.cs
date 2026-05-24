namespace BarkCloud.Web.Infrastructure;

/// <summary>
/// Чтение конфигурации, устойчивое к пустым значениям. В docker env-переменные
/// вида "${WEB_COOKIE_SECURE}" подставляются пустой строкой, если не заданы, —
/// тогда стандартный GetValue&lt;T&gt;/?? возвращает '' вместо дефолта.
/// </summary>
public static class ConfigExtensions
{
    /// <summary>Значение ключа или fallback, если ключ отсутствует/пустой.</summary>
    public static string Value(this IConfiguration config, string key, string fallback)
    {
        var value = config[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    /// <summary>true только при явном "true"; пусто/мусор/отсутствие → false.</summary>
    public static bool Flag(this IConfiguration config, string key)
        => bool.TryParse(config[key], out var value) && value;
}
