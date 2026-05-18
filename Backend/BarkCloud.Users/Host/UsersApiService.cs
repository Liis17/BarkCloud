using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Identity;
using BarkCloud.Users.Features.ChangeName;
using BarkCloud.Users.Features.ChangeUsername;
using BarkCloud.Users.Features.CheckExistEmail;
using BarkCloud.Users.Features.CheckExistUsername;
using BarkCloud.Users.Features.Devices.GetCurrentDevice;
using BarkCloud.Users.Features.Devices.GetDevices;
using BarkCloud.Users.Features.Devices.RenameDevice;
using BarkCloud.Users.Features.GetUser;
using BarkCloud.Users.Features.SetProfilePicture;

using Grpc.Core;

using MediatR;

using Microsoft.AspNetCore.Authorization;

namespace BarkCloud.Users.Host;

[Authorize(Policy = nameof(TokenType.User))]
public class UsersApiService : BarkCloud.Proto.Users.UsersApi.UsersApiBase
{
    private readonly IMediator _mediator;
    private readonly MetricsCollector _metrics;

    public UsersApiService(IMediator mediator, MetricsCollector metrics)
    {
        _mediator = mediator;
        _metrics = metrics;
    }

    public override async Task<GetUserResponse> GetUser(GetUserRequest request, ServerCallContext context)
    {
        _metrics.Increment("user_lookups");
        var query = new GetUserQuery { UserId = request.UserId == 0 ? null : request.UserId };
        return await _mediator.Send(query);
    }

    public override async Task<SetProfilePictureResponse> SetProfilePicture(SetProfilePictureRequest request, ServerCallContext context)
    {
        if (string.IsNullOrEmpty(request.FileId))
            _metrics.Increment("profile_avatar_removals");
        else
            _metrics.Increment("profile_avatar_updates");

        var command = new SetProfilePictureCommand
        {
            FileId = string.IsNullOrEmpty(request.FileId) ? null : Guid.Parse(request.FileId)
        };

        return await _mediator.Send(command);
    }

    [AllowAnonymous]
    public override Task<CheckExistResponse> CheckExistEmail(CheckExistEmailRequest request, ServerCallContext context)
    {
        _metrics.Increment("existence_checks");
        var command = new CheckExistEmailQuery() { Email = request.Email?.Trim() };

        return _mediator.Send(command);
    }

    [AllowAnonymous]
    public override Task<CheckExistResponse> CheckExistUsername(CheckExistUsernameRequest request,
        ServerCallContext context)
    {
        _metrics.Increment("existence_checks");
        var command = new CheckExistUsernameQuery() { Username = request.Username?.Trim() };

        return _mediator.Send(command);
    }

    public override async Task<ChangeNameResponse> ChangeName(ChangeNameRequest request, ServerCallContext context)
    {
        _metrics.Increment("profile_name_updates");
        var command = new ChangeNameCommand()
        {
            FirstName = request.FirstName?.Trim(),
            LastName = request.LastName?.Trim(),
        };

        await _mediator.Send(command);

        return new ChangeNameResponse();
    }

    public override async Task<ChangeUsernameResponse> ChangeUsername(ChangeUsernameRequest request, ServerCallContext context)
    {
        _metrics.Increment("profile_username_updates");
        var command = new ChangeUsernameCommand()
        {
            Username = request.Username?.Trim()
        };

        await _mediator.Send(command);

        return new ChangeUsernameResponse();
    }

    // Методы для работы с устройствами

    public override Task<GetDevicesResponse> GetDevices(GetDevicesRequest request, ServerCallContext context)
    {
        _metrics.Increment("device_lookups");
        var query = new GetDevicesQuery();
        return _mediator.Send(query);
    }

    public override Task<GetCurrentDeviceResponse> GetCurrentDevice(GetCurrentDeviceRequest request, ServerCallContext context)
    {
        _metrics.Increment("device_lookups");
        var query = new GetCurrentDeviceQuery();
        return _mediator.Send(query);
    }

    public override Task<RenameDeviceResponse> RenameDevice(RenameDeviceRequest request, ServerCallContext context)
    {
        _metrics.Increment("device_renames");
        var command = new RenameDeviceCommand
        {
            DeviceId = Guid.Parse(request.DeviceId),
            CustomName = request.CustomName
        };

        return _mediator.Send(command);
    }
}
