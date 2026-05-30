using BarkCloud.Web.Infrastructure;

namespace BarkCloud.Web.Tests.Infrastructure;

public class TemplateRendererTests
{
    private static string Render(string template, params (string Key, string? Value)[] vars)
        => new TemplateRenderer().Render(template, vars.ToDictionary(v => v.Key, v => v.Value));

    [Fact]
    public void Render_TripleBrace_SubstitutesRawWithoutEscaping()
    {
        // {{{ }}} используется для page_data_json — значение должно вставляться как есть.
        var json = "{\"a\":\"<b>\"}";

        Render("{{{ data }}}", ("data", json)).Should().Be(json);
    }

    [Theory]
    [InlineData("<", "\\x3c")]
    [InlineData(">", "\\x3e")]
    [InlineData("\"", "\\\"")]
    [InlineData("'", "\\'")]
    [InlineData("`", "\\`")]
    [InlineData("\\", "\\\\")]
    [InlineData("\n", "\\n")]
    [InlineData("\r", "\\r")]
    public void Render_DoubleBrace_JsEscapesSpecialChars(string input, string expected)
    {
        Render("{{ v }}", ("v", input)).Should().Be(expected);
    }

    [Fact]
    public void Render_DoubleBrace_PreventsScriptBreakout()
    {
        // Главная защита: значение не должно собрать закрывающий </script>.
        Render("{{ v }}", ("v", "</script>")).Should().Be("\\x3c/script\\x3e");
    }

    [Fact]
    public void Render_Default_UsedWhenKeyMissing()
    {
        Render("{{ v | default(\"fb\") }}").Should().Be("fb");
    }

    [Fact]
    public void Render_Default_UsedWhenValueEmpty()
    {
        Render("{{ v | default(\"fb\") }}", ("v", "")).Should().Be("fb");
    }

    [Fact]
    public void Render_Default_IgnoredWhenValuePresent()
    {
        Render("{{ v | default(\"fb\") }}", ("v", "real")).Should().Be("real");
    }

    [Fact]
    public void Render_MissingKeyWithoutDefault_ProducesEmpty()
    {
        Render("[{{ v }}]").Should().Be("[]");
    }

    [Fact]
    public void Render_DoesNotTouchJsxLikeDoubleBraces()
    {
        // Имя ограничено [\w.], поэтому style={{width:'10px'}} не должно зацепиться.
        const string template = "style={{width:'10px'}}";

        Render(template, ("width", "X")).Should().Be(template);
    }
}
