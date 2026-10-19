using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Features.ListOtpVerification;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Tests._Helpers;
using BarkCloud.Proto.Identity;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Features.ListOtpVerification;

public class ListOtpVerificationCommandHandlerTests
{
    private readonly Mock<IAuthPropertiesStorage> _authProps = new();
    private readonly ILogger<ListOtpVerificationCommandHandler> _logger = NullLogger<ListOtpVerificationCommandHandler>.Instance;

    private ListOtpVerificationCommandHandler CreateSut() => new(
        UserContextFactory.Create(42), _authProps.Object, _logger);

    [Fact]
    public async Task Handle_PropertiesMissing_ReturnsDisabledFlags()
    {
        _authProps.Setup(s => s.GetUserAuthProperties(42, CancellationToken.None)).ReturnsAsync((AuthUserProperty?)null);

        var response = await CreateSut().Handle(new ListOtpVerificationCommand(), CancellationToken.None);

        response.AuthenticatorEnabled.Should().BeFalse();
        response.EmailEnabled.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Handle_ReturnsConfiguredFlags(bool authenticatorEnabled, bool emailEnabled)
    {
        _authProps.Setup(s => s.GetUserAuthProperties(42, CancellationToken.None)).ReturnsAsync(new AuthUserProperty
        {
            UserId = 42,
            OtpEnabled = authenticatorEnabled,
            EmailOtpEnabled = emailEnabled
        });

        var response = await CreateSut().Handle(new ListOtpVerificationCommand(), CancellationToken.None);

        response.AuthenticatorEnabled.Should().Be(authenticatorEnabled);
        response.EmailEnabled.Should().Be(emailEnabled);
    }
}
