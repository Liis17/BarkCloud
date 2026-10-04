using BarkCloud.Notification.Configurations;

using Microsoft.Extensions.Configuration;

namespace BarkCloud.Notification.Tests.Configurations;

public class EmailConfigurationTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void AllowInsecure_BindsFromEmailSection_AndIsOffWhenMissing(string? value, bool expected)
    {
        var values = new Dictionary<string, string?> { ["Email:Host"] = "mail.example.test" };
        if (value is not null)
            values["Email:AllowInsecure"] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var settings = configuration.GetSection("Email").Get<EmailConfiguration>()!;

        settings.AllowInsecure.Should().Be(expected);
    }

    [Fact]
    public void AllowInsecure_InvalidValue_FailsBinding()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Email:AllowInsecure"] = "yes" })
            .Build();

        var act = () => configuration.GetSection("Email").Get<EmailConfiguration>();

        act.Should().Throw<InvalidOperationException>();
    }
}
