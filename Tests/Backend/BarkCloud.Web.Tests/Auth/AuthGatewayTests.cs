using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Identity;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Identity;
using BarkCloud.TestKit;
using BarkCloud.Web.Auth;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace BarkCloud.Web.Tests.Auth;

public class AuthGatewayTests
{
    private const string Secret = "test-secret-key-at-least-32-bytes-long!!";

    private readonly Mock<IdentityApi.IdentityApiClient> _identity = new();
    private readonly TokenRevocationCache _revocations = new();

    private AuthGateway CreateSut(string secret = Secret)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JwtSettings:SecretKey"] = secret,
                ["JwtSettings:Issuer"] = "bark",
                ["JwtSettings:Audience"] = "bark"
            })
            .Build();

        return new AuthGateway(_identity.Object, _revocations, config, NullLogger<AuthGateway>.Instance);
    }

    // User-JWT как у Identity (HS256, те же клеймы), но с управляемым iat.
    private static string Jwt(long userId = 42, string deviceId = "d1", DateTime? issuedAt = null, string secret = Secret,
        long? sessionId = null)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new(IdentityClaims.UserId, userId.ToString()),
            new(IdentityClaims.TokenType, TokenType.User.ToString()),
            new(IdentityClaims.DeviceId, deviceId)
        };
        if (sessionId.HasValue)
        {
            claims.Add(new Claim(IdentityClaims.SessionId, sessionId.Value.ToString()));
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            IssuedAt = issuedAt ?? now.AddMinutes(-10),
            NotBefore = now.AddMinutes(-10),
            Expires = now.AddMinutes(50),
            Issuer = "bark",
            Audience = "bark",
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), SecurityAlgorithms.HmacSha256)
        };

        var handler = new JwtSecurityTokenHandler();
        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    private static HttpContext HttpWithCookies(string access, string? refresh = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.Cookie = refresh is null
            ? $"{AuthGateway.AccessCookie}={access}"
            : $"{AuthGateway.AccessCookie}={access}; {AuthGateway.RefreshCookie}={refresh}";
        return http;
    }

    private void VerifyRefreshNotCalled()
        => _identity.Verify(
            c => c.CreateTokenAsync(It.IsAny<CreateTokenRequest>(), It.IsAny<Metadata>(), null, default),
            Times.Never);

    private static AuthResponse SuccessResponse() => new()
    {
        AccessToken = new Token { Value = "at", ExpirationDate = Timestamp.FromDateTime(DateTime.UtcNow.AddHours(1)) },
        RefreshToken = new Token { Value = "rt", ExpirationDate = Timestamp.FromDateTime(DateTime.UtcNow.AddDays(30)) }
    };

    private static RpcException RpcWithErrorCode(string? code)
    {
        var trailers = new Metadata();
        if (code is not null)
            trailers.Add("x-error-code", code);
        return new RpcException(new Status(StatusCode.Unauthenticated, "denied"), trailers);
    }

    [Fact]
    public async Task LoginAsync_Success_SetsCookiesAndReturnsSuccess()
    {
        _identity.Setup(c => c.AuthAsync(It.IsAny<AuthRequest>(), It.IsAny<Metadata>(), null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(SuccessResponse()));
        var http = new DefaultHttpContext();

        var result = await CreateSut().LoginAsync(http, "user", "pass", otp: null, remember: true);

        result.Outcome.Should().Be(LoginOutcome.Success);
        var setCookie = http.Response.Headers.SetCookie.ToString();
        setCookie.Should().Contain(AuthGateway.AccessCookie);
        setCookie.Should().Contain(AuthGateway.RefreshCookie);
    }

    // Коды берём из самих исключений Identity — тест ловит рассинхрон между
    // захардкоженными константами AuthGateway и реальными ErrorCode на проводе.
    public static IEnumerable<object[]> ErrorCases() =>
    [
        [new OtpCodeNeedException().ErrorCode, LoginOutcome.NeedsOtp],
        [new NotValidOtpCodeException().ErrorCode, LoginOutcome.WrongOtp],
        [new InvalidLoginOrPasswordException().ErrorCode, LoginOutcome.InvalidCredentials],
        [new PasswordAttemptsExceededException().ErrorCode, LoginOutcome.TooManyAttempts],
        [new TooManyRequestsException().ErrorCode, LoginOutcome.TooManyAttempts],
        ["00000000-0000-0000-0000-000000000000", LoginOutcome.Error]
    ];

    [Theory]
    [MemberData(nameof(ErrorCases))]
    public async Task LoginAsync_MapsErrorCodeToOutcome(string code, LoginOutcome expected)
    {
        _identity.Setup(c => c.AuthAsync(It.IsAny<AuthRequest>(), It.IsAny<Metadata>(), null, default))
            .Throws(RpcWithErrorCode(code));
        var http = new DefaultHttpContext();

        var result = await CreateSut().LoginAsync(http, "user", "pass", otp: null, remember: false);

        result.Outcome.Should().Be(expected);
    }

    [Fact]
    public async Task LoginAsync_NoErrorCodeTrailer_ReturnsError()
    {
        _identity.Setup(c => c.AuthAsync(It.IsAny<AuthRequest>(), It.IsAny<Metadata>(), null, default))
            .Throws(RpcWithErrorCode(null));
        var http = new DefaultHttpContext();

        var result = await CreateSut().LoginAsync(http, "user", "pass", otp: null, remember: false);

        result.Outcome.Should().Be(LoginOutcome.Error);
    }

    [Fact]
    public async Task AuthenticateAsync_ValidToken_ReturnsUser()
    {
        var http = HttpWithCookies(Jwt());

        var user = await CreateSut().AuthenticateAsync(http);

        user.Should().NotBeNull();
        user!.UserId.Should().Be(42);
        user.DeviceId.Should().Be("d1");
        VerifyRefreshNotCalled();
    }

    // F23: токен Identity подписан по UTF-8 байтам секрета; Web раньше проверял по ASCII.
    [Fact]
    public async Task AuthenticateAsync_UnicodeSecret_ReturnsUserForTokenSignedLikeIdentity()
    {
        const string unicodeSecret = "СекретныйКлючДляПодписиТокеновЮникод";
        var http = HttpWithCookies(Jwt(secret: unicodeSecret));

        var user = await CreateSut(unicodeSecret).AuthenticateAsync(http);

        user.Should().NotBeNull();
        user!.UserId.Should().Be(42);
        VerifyRefreshNotCalled();
    }

    [Fact]
    public async Task AuthenticateAsync_RevokedToken_NoRefreshCookie_ReturnsNull()
    {
        var http = HttpWithCookies(Jwt());
        _revocations.Revoke(42, "d1", DateTime.UtcNow, DateTime.UtcNow.AddHours(1));

        var user = await CreateSut().AuthenticateAsync(http);

        user.Should().BeNull();
        VerifyRefreshNotCalled();
    }

    [Fact]
    public async Task AuthenticateAsync_RevokedToken_RefreshRejected_ReturnsNull()
    {
        var http = HttpWithCookies(Jwt(), refresh: "rt");
        _revocations.Revoke(42, "d1", DateTime.UtcNow, DateTime.UtcNow.AddHours(1));
        _identity.Setup(c => c.CreateTokenAsync(It.IsAny<CreateTokenRequest>(), It.IsAny<Metadata>(), null, default))
            .Throws(RpcWithErrorCode(null));

        var user = await CreateSut().AuthenticateAsync(http);

        user.Should().BeNull();
    }

    [Fact]
    public async Task AuthenticateAsync_RevokedToken_RefreshWorks_ReturnsRefreshedUserAndSetsCookie()
    {
        var http = HttpWithCookies(Jwt(), refresh: "rt");
        _revocations.Revoke(42, "d1", DateTime.UtcNow, DateTime.UtcNow.AddHours(1));

        // Свежий токен выдан уже после отзыва (новый iat)
        var fresh = Jwt(issuedAt: DateTime.UtcNow.AddMinutes(1));
        _identity.Setup(c => c.CreateTokenAsync(It.IsAny<CreateTokenRequest>(), It.IsAny<Metadata>(), null, default))
            .Returns(GrpcCallHelpers.AsyncUnary(new CreateTokenResponse
            {
                AccessToken = new Token { Value = fresh, ExpirationDate = Timestamp.FromDateTime(DateTime.UtcNow.AddHours(1)) }
            }));

        var user = await CreateSut().AuthenticateAsync(http);

        user.Should().NotBeNull();
        user!.AccessToken.Should().Be(fresh);
        http.Response.Headers.SetCookie.ToString().Should().Contain(AuthGateway.AccessCookie);
    }

    [Fact]
    public async Task AuthenticateAsync_TokenIssuedAfterRevocation_ReturnsUser()
    {
        _revocations.Revoke(42, "d1", DateTime.UtcNow, DateTime.UtcNow.AddHours(1));
        var http = HttpWithCookies(Jwt(issuedAt: DateTime.UtcNow.AddMinutes(1)));

        var user = await CreateSut().AuthenticateAsync(http);

        user.Should().NotBeNull();
        VerifyRefreshNotCalled();
    }

    [Fact]
    public async Task AuthenticateAsync_OtherDeviceRevoked_ReturnsUser()
    {
        _revocations.Revoke(42, "d2", DateTime.UtcNow, DateTime.UtcNow.AddHours(1));
        var http = HttpWithCookies(Jwt(deviceId: "d1"));

        var user = await CreateSut().AuthenticateAsync(http);

        user.Should().NotBeNull();
    }

    [Fact]
    public async Task AuthenticateAsync_SessionRevocation_RejectsOldSessionAndAcceptsNewOneIssuedBefore()
    {
        _revocations.Revoke(42, "d1", DateTime.UtcNow, DateTime.UtcNow.AddHours(1), maxSessionId: 5);
        var oldSession = HttpWithCookies(Jwt(sessionId: 5));
        var noSid = HttpWithCookies(Jwt());
        // iat новой сессии не имеет значения: отзыв по сессии время не сравнивает.
        var newSession = HttpWithCookies(Jwt(sessionId: 6, issuedAt: DateTime.UtcNow.AddMinutes(-30)));

        (await CreateSut().AuthenticateAsync(oldSession)).Should().BeNull();
        (await CreateSut().AuthenticateAsync(noSid)).Should().BeNull();
        var user = await CreateSut().AuthenticateAsync(newSession);

        user.Should().NotBeNull();
        user!.SessionId.Should().Be(6);
    }
}
