using System.Text;
using System.Text.RegularExpressions;

namespace BarkCloud.Web.Infrastructure;

/// <summary>
/// Минимальный шаблонизатор под разметку страниц BarkCloud.
/// Поддерживает:
///   {{{ name }}}                     — RAW-подстановка (для page_data_json)
///   {{ name }}                       — значение
///   {{ name | default("fallback") }} — значение или fallback, если пусто
///
/// Все {{ }}-плейсхолдеры на страницах находятся внутри JS-строковых литералов
/// в &lt;script&gt;, поэтому значения экранируются по правилам JS-строки
/// (а не HTML), плюс защита от выхода из &lt;script&gt;.
/// Имя переменной ограничено [\w.] — это намеренно не даёт шаблонизатору
/// зацепить JSX-выражения вида style={{width:'...'}}.
/// </summary>
public sealed class TemplateRenderer
{
    private static readonly Regex Triple =
        new(@"\{\{\{\s*([\w.]+)\s*\}\}\}", RegexOptions.Compiled);

    private static readonly Regex Double =
        new(@"\{\{\s*([\w.]+)(?:\s*\|\s*default\(\s*""([^""]*)""\s*\))?\s*\}\}", RegexOptions.Compiled);

    public string Render(string template, IReadOnlyDictionary<string, string?> vars)
    {
        var afterTriple = Triple.Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            return vars.TryGetValue(key, out var v) ? v ?? string.Empty : string.Empty;
        });

        return Double.Replace(afterTriple, m =>
        {
            var key = m.Groups[1].Value;
            var fallback = m.Groups[2].Success ? m.Groups[2].Value : string.Empty;
            var value = vars.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v! : fallback;
            return JsEscape(value);
        });
    }

    private static string JsEscape(string value)
    {
        var sb = new StringBuilder(value.Length + 8);

        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\'': sb.Append("\\'"); break;
                case '`': sb.Append("\\`"); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '<': sb.Append("\\x3c"); break;   // не даём собрать </script>
                case '>': sb.Append("\\x3e"); break;
                default: sb.Append(ch); break;
            }
        }

        return sb.ToString();
    }
}
