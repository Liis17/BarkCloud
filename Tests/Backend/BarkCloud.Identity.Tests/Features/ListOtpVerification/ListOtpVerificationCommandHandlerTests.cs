using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Features.ListOtpVerification;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Tests._Helpers;
using BarkCloud.Proto.Identity;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using OtpNotCreatedException = BarkCloud.Identity.Persistence.Exceptions.OtpNotCreatedException;

namespace BarkCloud.Identity.Tests.Features.ListOtpVerification;

public class ListOtpVerificationCommandHandlerTests
{
    private readonly Mock<IAuthPropertiesStorage> _authProps = new();
    private readonly ILogger<ListOtpVerificationCommandHandler> _logger = NullLogger<ListOtpVerificationCommandHandler>.Instance;

    private ListOtpVerificationCommandHandler CreateSut() => new(
        UserContextFactory.Create(42), _authProps.Object, _logger);

    [Fact]
    public async Task Handle_PropertiesMissing_Throws()
    {
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync((AuthUserProperty?)null);

        var act = () => CreateSut().Handle(new ListOtpVerificationCommand(), default);

        await act.Should().ThrowAsync<OtpNotCreatedException>();
    }

    [Fact]
    public async Task Handle_ReturnsFlags()
    {
        _authProps.Setup(s => s.GetUserAuthProperties(42)).ReturnsAsync(new AuthUserProperty
        {
            UserId = 42,
            OtpEnabled = true,
            EmailOtpEnabled = false
        });

        var response = await CreateSut().Handle(new ListOtpVerificationCommand(), default);

        response.AuthenticatorEnabled.Should().BeTrue();
        response.EmailEnabled.Should().BeFalse();
    }
}
