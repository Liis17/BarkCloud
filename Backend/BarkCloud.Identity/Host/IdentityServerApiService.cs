using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Identity.Features.CreateSessionForUserServer;
using BarkCloud.Identity.Features.DisableOtpVerificationServer;
using BarkCloud.Identity.Features.ForceSetPasswordServer;
using BarkCloud.Identity.Features.GetActiveSessionsServer;
using BarkCloud.Identity.Features.ListOtpVerificationServer;
using BarkCloud.Identity.Features.RemoveActiveSessionServer;
using BarkCloud.Proto.Identity;
using BarkCloud.Shared.Identity;

using Grpc.Core;

using MediatR;

using Microsoft.AspNetCore.Authorization;

namespace BarkCloud.Identity.Host;

[Authorize(Policy = nameof(TokenType.Service))]
public class IdentityServerApiService : IdentityServerApi.IdentityServerApiBase
{
    private readonly IMediator _mediator;
    private readonly MetricsCollector _metrics;

    public IdentityServerApiService(IMediator mediator, MetricsCollector metrics)
    {
        _mediator = mediator;
        _metrics = metrics;
    }

    public override Task<ListOtpVerificationResponse> ListOtpVerificationServer(
        ListOtpVerificationServerRequest request, ServerCallContext context)
    {
        _metrics.Increment("server_otp_lookups");
        var command = new ListOtpVerificationServerCommand
        {
            UserId = request.UserId
        };

        return _mediator.Send(command);
    }

    public override Task<DisableOtpVerificationResponse> DisableOtpVerificationServer(
        DisableOtpVerificationServerRequest request, ServerCallContext context)
    {
        _metrics.Increment("server_otp_disable_attempts");
        var command = new DisableOtpVerificationServerCommand
        {
            UserId = request.UserId,
            OtpType = request.OtpType
        };

        return _mediator.Send(command);
    }

    public override Task<GetActiveSessionsResponse> GetActiveSessionsServer(
        GetActiveSessionsServerRequest request, ServerCallContext context)
    {
        _metrics.Increment("server_session_lookups");
        var command = new GetActiveSessionsServerCommand
        {
            UserId = request.UserId
        };

        return _mediator.Send(command);
    }

    public override Task<RemoveActiveSessionResponse> RemoveActiveSessionServer(
        RemoveActiveSessionServerRequest request, ServerCallContext context)
    {
        _metrics.Increment("server_session_removal_attempts");
        var command = new RemoveActiveSessionServerCommand
        {
            UserId = request.UserId,
            DeviceId = request.DeviceId
        };

        return _mediator.Send(command);
    }

    public override Task<CreateSessionForUserServerResponse> CreateSessionForUserServer(
        CreateSessionForUserServerRequest request, ServerCallContext context)
    {
        _metrics.Increment("server_session_creation_attempts");
        var command = new CreateSessionForUserServerCommand
        {
            UserId = request.UserId,
            DeviceId = request.DeviceId,
            DeviceName = request.DeviceName,
            OperationSystem = request.OperationSystem,
            AppName = request.AppName,
            IpAddress = request.IpAddress
        };

        return _mediator.Send(command);
    }

    public override Task<ForceSetPasswordServerResponse> ForceSetPasswordServer(
        ForceSetPasswordServerRequest request, ServerCallContext context)
    {
        _metrics.Increment("server_force_password_changes");
        return _mediator.Send(new ForceSetPasswordServerCommand
        {
            UserId = request.UserId,
            NewPassword = request.NewPassword
        });
    }
}
