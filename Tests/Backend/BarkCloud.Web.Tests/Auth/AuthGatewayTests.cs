using BarkCloud.Proto.Identity;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.TestKit;
using BarkCloud.Web.Auth;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Web.Tests.Auth;

public class AuthGatewayTests
{
    private readonly Mock<IdentityApi.IdentityApiClient> _identity = new();

    private AuthGateway CreateSut()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JwtSettings:SecretKey"] = "test-secret-key-at-least-32-bytes-long!!",
                ["JwtSettings:Issuer"] = "bark",
                ["JwtSettings:Audience"] = "bark"
            })
            .Build();

        return new AuthGateway(_identity.Object, config, NullLogger<AuthGateway>.Instance);
    }

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
}
