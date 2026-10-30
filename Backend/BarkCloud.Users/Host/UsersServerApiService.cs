using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Identity;
using BarkCloud.Users.Features.AddDraftUser;
using BarkCloud.Users.Features.ConfirmUser;
using BarkCloud.Users.Features.Devices.DeleteUserDevice;
using BarkCloud.Users.Features.Devices.GetUserDevices;
using BarkCloud.Users.Features.Devices.RegisterDevice;
using BarkCloud.Users.Features.FindByLogin;
using BarkCloud.Users.Features.GetUser;
using BarkCloud.Users.Features.GetUserContacts;
using BarkCloud.Users.Features.ListByIds;
using BarkCloud.Users.Features.OverrideDraftUser;
using BarkCloud.Users.Features.SetProfilePictureServer;

using Grpc.Core;

using MediatR;

using Microsoft.AspNetCore.Authorization;

namespace BarkCloud.Users.Host;

/// <summary>
/// Межсервисное API: класс — минимум «валидный сервисный токен для Users», каждый метод
/// дополнительно требует своё право (см. <see cref="ServicePermissions"/>).
/// </summary>
[Authorize(Policy = nameof(TokenType.Service))]
public class UsersServerApiService : UsersServerApi.UsersServerApiBase
{
    private readonly IMediator _mediator;
    private readonly MetricsCollector _metrics;

    public UsersServerApiService(
        IMediator mediator,
        MetricsCollector metrics)
    {
        _mediator = mediator;
        _metrics = metrics;
    }

    [Authorize(Policy = XAuthPolicies.UsersServerReadPolicy)]
    public override Task<FindByLoginResponse> FindByLogin(FindByLoginRequest request, ServerCallContext context)
    {
        _metrics.Increment("login_lookups");
        var command = new FindByLoginQuery() { Username = request.Username?.Trim(), Email = request.Email?.Trim() };

        return _mediator.Send(command);
    }

    [Authorize(Policy = XAuthPolicies.UsersServerWritePolicy)]
    public override async Task<AddDraftUserResponse> AddDraftUser(AddDraftUserRequest request, ServerCallContext context)
    {
        _metrics.Increment("drafts_create_requests");
        try
        {
            var command = new AddDraftUserCommand() { Username = request.Username?.Trim(), Email = request.Email?.Trim(), FirstName = request.FirstName?.Trim(), LastName = request.LastName?.Trim() };
            var response = await _mediator.Send(command);
            _metrics.Increment("drafts_created");
            _metrics.Set("last_draft_created_unix", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return response;
        }
        catch
        {
            _metrics.Increment("drafts_create_errors");
            throw;
        }
    }

    [Authorize(Policy = XAuthPolicies.UsersServerWritePolicy)]
    public override async Task<ConfirmUserResponse> ConfirmUser(ConfirmUserRequest request, ServerCallContext context)
    {
        _metrics.Increment("users_confirm_requests");
        try
        {
            var command = new ConfirmUserCommand() { UserId = request.UserId };
            await _mediator.Send(command);
            _metrics.Increment("users_confirmed");
            _metrics.Set("last_user_confirmed_unix", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return new ConfirmUserResponse();
        }
        catch
        {
            _metrics.Increment("users_confirm_errors");
            throw;
        }
    }

    [Authorize(Policy = XAuthPolicies.UsersServerReadPolicy)]
    public override async Task<GetByIdResponse> GetById(GetByIdRequest request, ServerCallContext context)
    {
        _metrics.Increment("user_lookups");
        var query = new GetUserQuery { UserId = request.UserId };
        var res = await _mediator.Send(query);

        return new GetByIdResponse { User = res.User };
    }

    [Authorize(Policy = XAuthPolicies.UsersServerReadPolicy)]
    public override Task<GetUserContactsResponse> GetUserContacts(GetUserContactsRequest request, ServerCallContext context)
    {
        _metrics.Increment("contact_lookups");
        var command = new GetUserContactsCommand()
        {
            UserId = request.UserId
        };

        return _mediator.Send(command);
    }

    [Authorize(Policy = XAuthPolicies.UsersServerWritePolicy)]
    public override Task<AddDraftUserResponse> OverrideDraftUser(AddDraftUserRequest request, ServerCallContext context)
    {
        _metrics.Increment("drafts_overridden");
        var command = new OverrideDraftUserCommand()
        {
            LastName = request.LastName?.Trim(),
            FirstName = request.FirstName?.Trim(),
            Email = request.Email?.Trim(),
            Username = request.Username?.Trim(),
        };

        return _mediator.Send(command);
    }

    [Authorize(Policy = XAuthPolicies.UsersServerReadPolicy)]
    public override async Task<ListByIdsResponse> ListByIds(ListByIdsRequest request, ServerCallContext context)
    {
        _metrics.Increment("user_lookups");
        var command = new ListByIdsCommand()
        {
            Ids = request.Ids.ToList()
        };

        return await _mediator.Send(command);
    }

    // Методы для работы с устройствами

    [Authorize(Policy = XAuthPolicies.UsersServerWritePolicy)]
    public override async Task<RegisterDeviceResponse> RegisterDevice(RegisterDeviceRequest request, ServerCallContext context)
    {
        _metrics.Increment("device_registrations");
        var response = await _mediator.Send(new RegisterDeviceCommand
        {
            DeviceId = Guid.Parse(request.DeviceId),
            UserId = request.UserId,
            OriginalName = request.OriginalName,
            AppName = request.AppName,
            OperationSystem = request.OperationSystem,
            Location = request.Location
        });
        _metrics.Set("last_device_registered_unix", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return response;
    }

    [Authorize(Policy = XAuthPolicies.UsersServerReadPolicy)]
    public override Task<GetUserDevicesResponse> GetUserDevices(GetUserDevicesRequest request, ServerCallContext context)
    {
        _metrics.Increment("device_lookups");
        var query = new GetUserDevicesQuery
        {
            UserId = request.UserId
        };

        return _mediator.Send(query);
    }

    [Authorize(Policy = XAuthPolicies.UsersServerWritePolicy)]
    public override Task<DeleteUserDeviceResponse> DeleteUserDevice(DeleteUserDeviceRequest request, ServerCallContext context)
    {
        _metrics.Increment("device_deletions");
        var command = new DeleteUserDeviceCommand
        {
            DeviceId = Guid.Parse(request.DeviceId),
            UserId = request.UserId
        };

        return _mediator.Send(command);
    }

    [Authorize(Policy = XAuthPolicies.UsersServerWritePolicy)]
    public override Task<SetProfilePictureServerResponse> SetProfilePictureServer(SetProfilePictureServerRequest request, ServerCallContext context)
    {
        _metrics.Increment("profile_avatar_updates");
        var command = new SetProfilePictureServerCommand
        {
            UserId = request.UserId,
            ProfilePictureUrl = request.ProfilePictureUrl,
            ProfilePicturePreviewUrl = request.ProfilePicturePreviewUrl
        };

        return _mediator.Send(command);
    }
}
