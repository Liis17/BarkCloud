namespace BarkCloud.Web.Auth;

/// <summary>
/// Адрес возврата после входа (<c>/login?returnUrl=…</c>). Принимается только локальный путь
/// этого же сайта, иначе — <see cref="Default"/>; это защищает от open redirect.
/// </summary>
public static class LoginReturnUrl
{
    public const string Default = "/photos";
    public const string QueryKey = "returnUrl";
    public const string FormKey = "return_url";
    private const int MaxLength = 2048;

    private static readonly string[] AuthPaths = ["/login", "/logout", "/register", "/forgot"];

    /// <summary>Безопасный локальный путь или <see cref="Default"/>.</summary>
    public static string Resolve(string? value) => IsSafe(value) ? value! : Default;

    /// <summary>Адрес страницы входа с возвратом на <paramref name="pathAndQuery"/>, если он безопасен.</summary>
    public static string LoginPath(string? pathAndQuery)
        => IsSafe(pathAndQuery) && pathAndQuery != Default
            ? $"/login?{QueryKey}={Uri.EscapeDataString(pathAndQuery!)}"
            : "/login";

    public static bool IsSafe(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength || value[0] != '/')
            return false;
        // «//host» и «/\host» браузер трактует как адрес другого хоста.
        if (value.Length > 1 && value[1] is '/' or '\\')
            return false;
        // Обратный слеш браузер нормализует в «/», а табуляции и переводы строк вырезает
        // («/\t/evil» → «//evil»), поэтому такие символы не допускаются вовсе.
        foreach (var c in value)
            if (c == '\\' || char.IsControl(c) || char.IsWhiteSpace(c))
                return false;
        if (!Uri.TryCreate(value, UriKind.Relative, out _))
            return false;
        // Возврат на страницы входа дал бы петлю.
        var path = value.AsSpan(0, value.IndexOfAny(['?', '#']) is var end and >= 0 ? end : value.Length);
        foreach (var authPath in AuthPaths)
            if (path.Equals(authPath, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(authPath + "/", StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }
}
