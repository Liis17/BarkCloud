using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Features.ListOtpVerificationServer;
using BarkCloud.Identity.Persistence.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Features.ListOtpVerificationServer;

public class ListOtpVerificationServerCommandHandlerTests
{
    private readonly Mock<IAuthPropertiesStorage> _authProps = new();

    private ListOtpVerificationServerCommandHandler CreateSut() => new(
        _authProps.Object,
        NullLogger<ListOtpVerificationServerCommandHandler>.Instance);

    [Fact]
    public async Task Handle_NoProperties_ReturnsAllDisabled()
    {
        _authProps.Setup(s => s.GetUserAuthProperties(7)).ReturnsAsync((AuthUserProperty?)null);

        var response = await CreateSut().Handle(new ListOtpVerificationServerCommand { UserId = 7 }, default);

        response.AuthenticatorEnabled.Should().BeFalse();
        response.EmailEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_WithProperties_ReturnsFlags()
    {
        _authProps.Setup(s => s.GetUserAuthProperties(7)).ReturnsAsync(new AuthUserProperty
        {
            UserId = 7, OtpEnabled = true, EmailOtpEnabled = false
        });

        var response = await CreateSut().Handle(new ListOtpVerificationServerCommand { UserId = 7 }, default);

        response.AuthenticatorEnabled.Should().BeTrue();
        response.EmailEnabled.Should().BeFalse();
    }
}
