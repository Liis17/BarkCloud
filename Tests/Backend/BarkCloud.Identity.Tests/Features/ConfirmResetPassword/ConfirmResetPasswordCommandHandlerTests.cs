using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Features.ConfirmResetPassword;
using BarkCloud.Identity.Features.CreateToken;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Shared.Exceptions.Identity;

using MediatR;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using OtpNet;

using DomainResetPassword = BarkCloud.Identity.Domain.ResetPassword;
using OtpType = BarkCloud.Identity.Domain.OtpType;

namespace BarkCloud.Identity.Tests.Features.ConfirmResetPassword;

public class ConfirmResetPasswordCommandHandlerTests
{
    private readonly Mock<IResetPasswordsStorage> _resets = new();
    private readonly Mock<IAuthPropertiesStorage> _authProps = new();
    private readonly Mock<IPasswordsStorage> _passwords = new();
    private readonly Mock<IRefreshTokensStorage> _refreshTokens = new();
    private readonly Mock<IMediator> _mediator = new();
    private readonly MetricsCollector _metrics = new();
    private readonly ILogger<ConfirmResetPasswordCommandHandler> _logger = NullLogger<ConfirmResetPasswordCommandHandler>.Instance;

    private ConfirmResetPasswordCommandHandler CreateSut(RequestContext? ctx = null) => new(
        _resets.Object, _authProps.Object, _passwords.Object, _refreshTokens.Object,
        _mediator.Object, ctx ?? FullContext(), _metrics, _logger);

    private static RequestContext FullContext() => new()
    {
        DeviceName = "Pixel",
        OperationSystem = "Android",
        AppName = "BarkCloud",
        AppVersion = "1.0",
        DeviceId = "device-1"
    };

    [Fact]
    public async Task Handle_NoDeviceName_Throws()
    {
        var ctx = new RequestContext { DeviceName = null };

        var act = () => CreateSut(ctx).Handle(new ConfirmResetPasswordCommand { ResetId = Guid.NewGuid(), OtpCode = "0" }, default);

        await act.Should().ThrowAsync<XDeviceNameIsRequiredException>();
    }

    [Fact]
    public async Task Handle_ResetIdNotFound_Throws()
    {
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync((DomainResetPassword?)null);

        var act = () => CreateSut().Handle(new ConfirmResetPasswordCommand { ResetId = id, OtpCode = "0" }, default);

        await act.Should().ThrowAsync<ResetIdNotFoundException>();
    }

    [Fact]
    public async Task Handle_AlreadyApproved_Throws()
    {
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(new DomainResetPassword
        {
            Id = id,
            UserId = 42,
            IsApproved = true,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        });

        var act = () => CreateSut().Handle(new ConfirmResetPasswordCommand { ResetId = id, OtpCode = "0" }, default);

        await act.Should().ThrowAsync<ResetIdHasIsApprovedException>();
    }

    [Fact]
    public async Task Handle_Expired_Throws()
    {
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(new DomainResetPassword
        {
            Id = id,
            UserId = 42,
            ExpiresAt = DateTime.UtcNow.AddMinutes(-1)
        });

        var act = () => CreateSut().Handle(new ConfirmResetPasswordCommand { ResetId = id, OtpCode = "0" }, default);

        await act.Should().ThrowAsync<ResetIdExpiredException>();
    }

    [Fact]
    public async Task Handle_AuthenticatorWrongCode_Throws()
    {
        var id = Guid.NewGuid();
        var key = KeyGeneration.GenerateRandomKey(20);
        var secret = Base32Encoding.ToString(key);
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(new DomainResetPassword
        {
            Id = id,
            UserId = 42,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            OtpType = OtpType.Authenticator
        });
        _authProps.Setup(s => s.GetOtpSecretKey(42)).ReturnsAsync(secret);

        var act = () => CreateSut().Handle(new ConfirmResetPasswordCommand { ResetId = id, OtpCode = "000000" }, default);

        await act.Should().ThrowAsync<NotValidOtpCodeException>();
    }

    [Fact]
    public async Task Handle_EmailWrongCode_Throws()
    {
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(new DomainResetPassword
        {
            Id = id,
            UserId = 42,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            OtpType = OtpType.Email,
            OtpCode = "123456"
        });

        var act = () => CreateSut().Handle(new ConfirmResetPasswordCommand { ResetId = id, OtpCode = "999999" }, default);

        await act.Should().ThrowAsync<NotValidOtpCodeException>();
    }

    [Fact]
    public async Task Handle_EmailValidCode_ClearsPasswordAndReturnsTokens()
    {
        var id = Guid.NewGuid();
        _resets.Setup(s => s.GetResetPassword(id)).ReturnsAsync(new DomainResetPassword
        {
            Id = id,
            UserId = 42,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            OtpType = OtpType.Email,
            OtpCode = "123456"
        });
        _mediator
            .Setup(m => m.Send(It.IsAny<CreateTokenCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateTokenResponse { AccessToken = new Token { Value = "access" } });

        var response = await CreateSut().Handle(
            new ConfirmResetPasswordCommand { ResetId = id, OtpCode = "123456" }, default);

        response.AccessToken.Value.Should().Be("access");
        response.RefreshToken.Value.Should().NotBeNullOrWhiteSpace();
        _resets.Verify(s => s.SetApproved(id), Times.Once);
        _passwords.Verify(s => s.ClearUserPasswordHash(42), Times.Once);
        _refreshTokens.Verify(s => s.CreateNewRefreshToken(It.IsAny<string>(), 42, "device-1", It.IsAny<int>()), Times.Once);
        _metrics.SnapshotAndReset().Should().ContainKey("password_resets_confirmed");
    }
}
