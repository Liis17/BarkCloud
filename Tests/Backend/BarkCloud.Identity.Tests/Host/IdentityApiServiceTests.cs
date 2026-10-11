using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Identity.Features.Auth;
using BarkCloud.Identity.Features.ConfirmAccount;
using BarkCloud.Identity.Features.CompleteWebAuthnAssertion;
using BarkCloud.Identity.Features.CreateAccount;
using BarkCloud.Identity.Host;
using BarkCloud.Identity.Services;
using BarkCloud.Identity.Settings;
using BarkCloud.Proto.Identity;
using BarkCloud.TestKit;

using Grpc.Core;

using MediatR;

namespace BarkCloud.Identity.Tests.Host;

public class IdentityApiServiceTests
{
    [Fact]
    public async Task Auth_ForwardsCallCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(It.IsAny<AuthCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthResponse());
        var sut = new IdentityApiService(mediator.Object, new JwtService(new JwtSettings()), new MetricsCollector());

        await sut.Auth(new AuthRequest(), new TestServerCallContext(cancellationToken: cts.Token))
            .WaitAsync(TimeSpan.FromSeconds(2));

        mediator.Verify(m => m.Send(It.IsAny<AuthCommand>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task CompleteWebAuthnAssertion_ForwardsCallCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(It.IsAny<CompleteWebAuthnAssertionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthResponse());
        var sut = new IdentityApiService(mediator.Object, new JwtService(new JwtSettings()), new MetricsCollector());

        await sut.CompleteWebAuthnAssertion(new CompleteWebAuthnAssertionRequest(),
                new TestServerCallContext(cancellationToken: cts.Token))
            .WaitAsync(TimeSpan.FromSeconds(2));

        mediator.Verify(m => m.Send(It.IsAny<CompleteWebAuthnAssertionCommand>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task Auth_PropagatesMediatorCancellation()
    {
        using var cts = new CancellationTokenSource();
        var failure = new OperationCanceledException(cts.Token);
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(It.IsAny<AuthCommand>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromException<AuthResponse>(failure));
        var sut = new IdentityApiService(mediator.Object, new JwtService(new JwtSettings()), new MetricsCollector());

        var act = () => sut.Auth(new AuthRequest(), new TestServerCallContext(cancellationToken: cts.Token))
            .WaitAsync(TimeSpan.FromSeconds(2));

        var exception = await act.Should().ThrowAsync<OperationCanceledException>();
        exception.Which.CancellationToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task CreateAccountWithPassword_ForwardsPasswordAndCancellation()
    {
        using var cts = new CancellationTokenSource();
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(It.IsAny<CreateAccountCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateAccountResponse());
        var sut = new IdentityApiService(mediator.Object, new JwtService(new JwtSettings()), new MetricsCollector());

        await sut.CreateAccountWithPassword(new CreateAccountRequest
        {
            Username = "u", Email = "u@e", InitialPassword = "secret1"
        }, new TestServerCallContext(cancellationToken: cts.Token));

        mediator.Verify(m => m.Send(It.Is<CreateAccountCommand>(c => c.RequiresInitialPassword
            && c.InitialPassword == "secret1"), cts.Token), Times.Once);
    }

    [Fact]
    public async Task LegacyCreateAccount_IgnoresInitialPasswordField()
    {
        using var cts = new CancellationTokenSource();
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(It.IsAny<CreateAccountCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateAccountResponse());
        var sut = new IdentityApiService(mediator.Object, new JwtService(new JwtSettings()), new MetricsCollector());

        await sut.CreateAccount(new CreateAccountRequest
        {
            Username = "u", Email = "u@e", InitialPassword = "secret1"
        }, new TestServerCallContext(cancellationToken: cts.Token));

        mediator.Verify(m => m.Send(It.Is<CreateAccountCommand>(c => !c.RequiresInitialPassword
            && c.InitialPassword == null), cts.Token), Times.Once);
    }

    [Fact]
    public async Task LegacyConfirmAccount_IgnoresInitialPasswordField()
    {
        using var cts = new CancellationTokenSource();
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(It.IsAny<ConfirmAccountCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConfirmAccountResponse());
        var sut = new IdentityApiService(mediator.Object, new JwtService(new JwtSettings()), new MetricsCollector());

        await sut.ConfirmAccount(new ConfirmAccountRequest
        {
            CodeId = "id", CodeValue = "123456", InitialPassword = "secret1"
        }, new TestServerCallContext(cancellationToken: cts.Token));

        mediator.Verify(m => m.Send(It.Is<ConfirmAccountCommand>(c => !c.RequiresInitialPassword
            && c.InitialPassword == null), cts.Token), Times.Once);
    }

    [Fact]
    public async Task ConfirmAccountWithPassword_ForwardsPasswordAndCancellation()
    {
        using var cts = new CancellationTokenSource();
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(It.IsAny<ConfirmAccountCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConfirmAccountResponse());
        var sut = new IdentityApiService(mediator.Object, new JwtService(new JwtSettings()), new MetricsCollector());

        await sut.ConfirmAccountWithPassword(new ConfirmAccountRequest
        {
            CodeId = "id", CodeValue = "123456", InitialPassword = "secret1"
        }, new TestServerCallContext(cancellationToken: cts.Token));

        mediator.Verify(m => m.Send(It.Is<ConfirmAccountCommand>(c => c.RequiresInitialPassword
            && c.InitialPassword == "secret1"), cts.Token), Times.Once);
    }

    [Fact]
    public async Task CompleteWebAuthnAssertion_PropagatesMediatorCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(It.IsAny<CompleteWebAuthnAssertionCommand>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromCanceled<AuthResponse>(cts.Token));
        var sut = new IdentityApiService(mediator.Object, new JwtService(new JwtSettings()), new MetricsCollector());

        var act = () => sut.CompleteWebAuthnAssertion(new CompleteWebAuthnAssertionRequest(),
                new TestServerCallContext(cancellationToken: cts.Token))
            .WaitAsync(TimeSpan.FromSeconds(2));

        var exception = await act.Should().ThrowAsync<OperationCanceledException>();
        exception.Which.CancellationToken.Should().Be(cts.Token);
        mediator.Verify(m => m.Send(It.IsAny<CompleteWebAuthnAssertionCommand>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task CompleteWebAuthnAssertion_PropagatesMediatorFailure()
    {
        var failure = new RpcException(new Status(StatusCode.Unavailable, "mediator failed"));
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(It.IsAny<CompleteWebAuthnAssertionCommand>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromException<AuthResponse>(failure));
        var sut = new IdentityApiService(mediator.Object, new JwtService(new JwtSettings()), new MetricsCollector());

        var act = () => sut.CompleteWebAuthnAssertion(new CompleteWebAuthnAssertionRequest(), new TestServerCallContext())
            .WaitAsync(TimeSpan.FromSeconds(2));

        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.Should().BeSameAs(failure);
    }
}
