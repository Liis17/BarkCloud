using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Identity;
using BarkCloud.Users.Features.ChangeBio;
using BarkCloud.Users.Features.ChangeName;
using BarkCloud.Users.Features.ChangeUsername;
using BarkCloud.Users.Features.CheckExistEmail;
using BarkCloud.Users.Features.CheckExistUsername;
using BarkCloud.Users.Features.DeleteAccount;
using BarkCloud.Users.Features.Devices.DeleteDevice;
using BarkCloud.Users.Features.Devices.GetCurrentDevice;
using BarkCloud.Users.Features.Devices.GetDevices;
using BarkCloud.Users.Features.Devices.RenameDevice;
using BarkCloud.Users.Features.Devices.SetFirebaseToken;
using BarkCloud.Users.Features.GetUser;
using BarkCloud.Users.Features.Privacy.GetPrivacySettings;
using BarkCloud.Users.Features.Privacy.UpdatePrivacySettings;
using BarkCloud.Users.Features.SearchUsers;
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

    public override async Task<ChangeBioResponse> ChangeBio(ChangeBioRequest request, ServerCallContext context)
    {
        _metrics.Increment("profile_bio_updates");
        await _mediator.Send(new ChangeBioCommand { Bio = request.Bio });

        return new ChangeBioResponse();
    }

    public override Task<SearchUsersResponse> SearchUsers(SearchUsersRequest request, ServerCallContext context)
    {
        _metrics.Increment("user_searches");
        var query = new SearchUsersQuery
        {
            Query = request.Query,
            Limit = request.Limit
        };

        return _mediator.Send(query);
    }

    public override async Task<DeleteAccountResponse> DeleteAccount(DeleteAccountRequest request, ServerCallContext context)
    {
        _metrics.Increment("account_deletions");
        await _mediator.Send(new DeleteAccountCommand());

        return new DeleteAccountResponse();
    }

    public override Task<GetPrivacySettingsResponse> GetPrivacySettings(GetPrivacySettingsRequest request, ServerCallContext context)
    {
        _metrics.Increment("privacy_lookups");
        return _mediator.Send(new GetPrivacySettingsQuery());
    }

    public override Task<UpdatePrivacySettingsResponse> UpdatePrivacySettings(UpdatePrivacySettingsRequest request, ServerCallContext context)
    {
        _metrics.Increment("privacy_updates");
        var settings = request.Settings ?? new PrivacySettings();
        var command = new UpdatePrivacySettingsCommand
        {
            ProfileVisibility = (Domain.PrivacyVisibility)settings.ProfileVisibility,
            EmailVisibility = (Domain.PrivacyVisibility)settings.EmailVisibility,
            LastSeenVisibility = (Domain.PrivacyVisibility)settings.LastSeenVisibility,
            SearchableByUsername = settings.SearchableByUsername
        };

        return _mediator.Send(command);
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

    public override Task<DeleteDeviceResponse> DeleteDevice(DeleteDeviceRequest request, ServerCallContext context)
    {
        _metrics.Increment("device_self_deletions");
        var command = new DeleteDeviceCommand
        {
            DeviceId = Guid.Parse(request.DeviceId)
        };

        return _mediator.Send(command);
    }

    public override Task<SetFirebaseTokenResponse> SetFirebaseToken(SetFirebaseTokenRequest request, ServerCallContext context)
    {
        _metrics.Increment("firebase_token_updates");
        var command = new SetFirebaseTokenCommand
        {
            FirebaseToken = request.FirebaseToken
        };

        return _mediator.Send(command);
    }
}
