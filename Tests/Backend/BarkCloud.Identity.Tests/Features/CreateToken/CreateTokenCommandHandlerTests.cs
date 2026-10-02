using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Features.CreateToken;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Settings;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Identity;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using System.IdentityModel.Tokens.Jwt;

namespace BarkCloud.Identity.Tests.Features.CreateToken;

public class CreateTokenCommandHandlerTests
{
    private readonly Mock<IRefreshTokensStorage> _refreshTokens = new();
    private readonly JwtService _jwt = new(new JwtSettings
    {
        SecretKey = "supersecretkey_at_least_32_chars_long_for_hs256!!",
        Issuer = "bark",
        Audience = "bark",
        ExpiryMinutes = 60
    });
    private readonly MetricsCollector _metrics = new();
    private readonly ILogger<CreateTokenCommandHandler> _logger = NullLogger<CreateTokenCommandHandler>.Instance;

    private CreateTokenCommandHandler CreateSut()
        => new(_refreshTokens.Object, _jwt, _metrics, _logger);

    [Fact]
    public async Task Handle_RefreshTokenNotFound_Throws()
    {
        _refreshTokens.Setup(s => s.FindRefreshToken(It.IsAny<string>())).ReturnsAsync((RefreshToken?)null);

        var act = () => CreateSut().Handle(new CreateTokenCommand { RefreshToken = "missing" }, default);

        await act.Should().ThrowAsync<InvalidRefreshTokenException>();
    }

    [Fact]
    public async Task Handle_RefreshTokenExpired_Throws()
    {
        _refreshTokens.Setup(s => s.FindRefreshToken("t")).ReturnsAsync(new RefreshToken
        {
            UserId = 1,
            DeviceId = "d",
            Value = "t",
            ExpiresAt = DateTime.UtcNow.AddMinutes(-1)
        });

        var act = () => CreateSut().Handle(new CreateTokenCommand { RefreshToken = "t" }, default);

        await act.Should().ThrowAsync<InvalidRefreshTokenException>();
    }

    [Fact]
    public async Task Handle_RefreshTokenWithoutDevice_Throws()
    {
        _refreshTokens.Setup(s => s.FindRefreshToken("t")).ReturnsAsync(new RefreshToken
        {
            UserId = 1,
            DeviceId = string.Empty,
            Value = "t",
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        });

        var act = () => CreateSut().Handle(new CreateTokenCommand { RefreshToken = "t" }, default);

        await act.Should().ThrowAsync<InvalidRefreshTokenException>();
    }

    [Fact]
    public async Task Handle_HappyPath_ReturnsAccessToken()
    {
        _refreshTokens.Setup(s => s.FindRefreshToken("t")).ReturnsAsync(new RefreshToken
        {
            Id = 17,
            UserId = 42,
            DeviceId = "device-1",
            Value = "t",
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        });

        var response = await CreateSut().Handle(new CreateTokenCommand { RefreshToken = "t" }, default);

        response.AccessToken.Value.Should().NotBeNullOrWhiteSpace();
        // sid access-токена — Id refresh-строки: по нему отзывается старая сессия устройства при сбросе пароля.
        new JwtSecurityTokenHandler().ReadJwtToken(response.AccessToken.Value).Claims
            .Should().Contain(c => c.Type == IdentityClaims.SessionId && c.Value == "17");
        _metrics.SnapshotAndReset().Should().ContainKey("tokens_refreshed");
    }
}
