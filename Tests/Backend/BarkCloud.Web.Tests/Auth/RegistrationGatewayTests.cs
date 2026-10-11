using BarkCloud.Proto.Identity;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.TestKit;
using BarkCloud.Web.Auth;

using Grpc.Core;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BarkCloud.Web.Tests.Auth;

public sealed class RegistrationGatewayTests
{
    [Fact]
    public async Task CreateAccountWithPasswordUnimplementedDoesNotCallLegacyRegistrationMethods()
    {
        var identity = new Mock<IdentityApi.IdentityApiClient>(MockBehavior.Strict);
        identity.Setup(client => client.CreateAccountWithPasswordAsync(It.IsAny<CreateAccountRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Throws(Unimplemented());
        var gateway = CreateGateway(identity.Object);

        var result = await gateway.BeginAsync(new DefaultHttpContext(), "", "", "user",
            "user@example.test", "secret-password");

        result.Outcome.Should().Be(RegistrationOutcome.Error);
        result.Message.Should().Contain("временно недоступна");
        identity.Verify(client => client.CreateAccountWithPasswordAsync(It.IsAny<CreateAccountRequest>(),
            It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
        identity.Verify(client => client.CreateAccountAsync(It.IsAny<CreateAccountRequest>(),
            It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        identity.Verify(client => client.SetPasswordAsync(It.IsAny<SetPasswordRequest>(),
            It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmAccountWithPasswordUnimplementedDoesNotCallLegacyConfirmationMethods()
    {
        var identity = new Mock<IdentityApi.IdentityApiClient>(MockBehavior.Strict);
        identity.Setup(client => client.ConfirmAccountWithPasswordAsync(It.IsAny<ConfirmAccountRequest>(),
                It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Throws(Unimplemented());
        var gateway = CreateGateway(identity.Object);

        var result = await gateway.ConfirmAsync(new DefaultHttpContext(), "private-code-id", "123456",
            "secret-password");

        result.Outcome.Should().Be(RegistrationOutcome.Error);
        result.Message.Should().Contain("временно недоступна");
        identity.Verify(client => client.ConfirmAccountWithPasswordAsync(It.IsAny<ConfirmAccountRequest>(),
            It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
        identity.Verify(client => client.ConfirmAccountAsync(It.IsAny<ConfirmAccountRequest>(),
            It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        identity.Verify(client => client.SetPasswordAsync(It.IsAny<SetPasswordRequest>(),
            It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static RegistrationGateway CreateGateway(IdentityApi.IdentityApiClient identity)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["JwtSettings:SecretKey"] = "registration-gateway-test-signing-secret-at-least-32-bytes",
            ["Features:EmailEnabled"] = "true"
        }).Build();
        var auth = new AuthGateway(identity, new TokenRevocationCache(), configuration,
            NullLogger<AuthGateway>.Instance);
        return new RegistrationGateway(identity, auth, configuration,
            NullLogger<RegistrationGateway>.Instance);
    }

    private static RpcException Unimplemented()
        => new(new Status(StatusCode.Unimplemented, "atomic registration RPC is unavailable"));
}
