using System.Text;
using System.Text.RegularExpressions;

namespace BarkCloud.Files.Services;

/// <summary>Единые правила нормализации пользовательского поискового текста.</summary>
public static partial class SearchText
{
    public static string Normalize(string? value)
    {
        return CollapseWhitespace(value).ToLowerInvariant();
    }

    public static string CollapseWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return Whitespace().Replace(value.Normalize(NormalizationForm.FormKC).Trim(), " ");
    }

    public static bool IsSearchableQuery(string? value) => Normalize(value).Length >= 2;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
