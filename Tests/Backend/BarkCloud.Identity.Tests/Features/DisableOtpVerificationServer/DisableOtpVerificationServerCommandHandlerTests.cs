using BarkCloud.Identity.Features.DisableOtpVerificationServer;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Proto.Identity;

using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Identity.Tests.Features.DisableOtpVerificationServer;

public class DisableOtpVerificationServerCommandHandlerTests
{
    private readonly Mock<IAuthPropertiesStorage> _authProps = new();

    private DisableOtpVerificationServerCommandHandler CreateSut() => new(
        _authProps.Object,
        NullLogger<DisableOtpVerificationServerCommandHandler>.Instance);

    [Fact]
    public async Task Handle_AuthenticatorType_DisablesOtp()
    {
        await CreateSut().Handle(new DisableOtpVerificationServerCommand
        {
            UserId = 7, OtpType = OtpTypeId.Authenticator
        }, default);

        _authProps.Verify(s => s.DisableOtp(7), Times.Once);
        _authProps.Verify(s => s.DisableEmailOtp(It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public async Task Handle_EmailType_DisablesEmailOtp()
    {
        await CreateSut().Handle(new DisableOtpVerificationServerCommand
        {
            UserId = 7, OtpType = OtpTypeId.Email
        }, default);

        _authProps.Verify(s => s.DisableEmailOtp(7), Times.Once);
        _authProps.Verify(s => s.DisableOtp(It.IsAny<long>()), Times.Never);
    }
}
