using BarkCloud.Notification.Configurations;

using System.Diagnostics;

namespace BarkCloud.Notification.Tests.Configurations;

public class EmailConfigurationTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("true", true)]
    [InlineData(" TRUE ", true)]
    public void ParseAllowInsecure_OnlyExplicitTrueEnablesInsecureSmtp(string? value, bool expected)
    {
        EmailConfiguration.ParseAllowInsecure(value).Should().Be(expected);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("invalid")]
    public void ParseAllowInsecure_InvalidValuePreventsStartup(string value)
    {
        var act = () => EmailConfiguration.ParseAllowInsecure(value);

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public async Task Startup_InvalidEnvironmentFlag_FailsBeforeLoadingRemoteConfiguration()
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add(typeof(EmailConfiguration).Assembly.Location);
        startInfo.Environment["SMTP_ALLOW_INSECURE"] = "invalid";
        using var process = Process.Start(startInfo)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            process.ExitCode.Should().NotBe(0);
            (await error).Should().Contain("SMTP_ALLOW_INSECURE").And.Contain(nameof(FormatException));
            await output;
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }
}
