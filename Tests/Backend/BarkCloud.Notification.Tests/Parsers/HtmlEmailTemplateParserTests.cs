using BarkCloud.Notification.Parsers;
using BarkCloud.Shared.Queue.Notifications;

namespace BarkCloud.Notification.Tests.Parsers;

public class HtmlEmailTemplateParserTests : IDisposable
{
    private readonly string _templatesDir = Path.Combine(Environment.CurrentDirectory, "Templates");
    private readonly string _templateFile;

    public HtmlEmailTemplateParserTests()
    {
        // Parser читает шаблон из {CurrentDirectory}/Templates/successful_login.html
        Directory.CreateDirectory(_templatesDir);
        _templateFile = Path.Combine(_templatesDir, "successful_login.html");
        File.WriteAllText(_templateFile,
            "Привет, ꟿꟿꟿusernameꟿꟿꟿ! Год: ꟿꟿꟿcurrentyearꟿꟿꟿ. ꟿꟿꟿunknownꟿꟿꟿ");
    }

    public void Dispose()
    {
        if (File.Exists(_templateFile))
            File.Delete(_templateFile);
    }

    [Fact]
    public async Task Parse_SubstitutesPayloadAndCurrentYear_LeavesUnknownPlaceholder()
    {
        var sut = new HtmlEmailTemplateParser();

        var result = await sut.Parse(NotificationType.SuccessfulLogin, new Dictionary<string, string>
        {
            ["username"] = "barker"
        });

        result.Should().Contain("Привет, barker!");
        result.Should().Contain($"Год: {DateTime.UtcNow.Year}.");
        result.Should().Contain("ꟿꟿꟿunknownꟿꟿꟿ");
    }

    [Fact]
    public async Task Parse_HtmlEncodesPayloadValues()
    {
        var sut = new HtmlEmailTemplateParser();

        var result = await sut.Parse(NotificationType.SuccessfulLogin, new Dictionary<string, string>
        {
            ["username"] = "<script>"
        });

        result.Should().Contain("&lt;script&gt;");
        result.Should().NotContain("<script>");
    }
}
