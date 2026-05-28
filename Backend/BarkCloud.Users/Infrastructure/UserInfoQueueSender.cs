using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Shared.Queue.Users;

using MassTransit;

namespace BarkCloud.Users.Infrastructure;

public class UserInfoQueueSender
{
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly MetricsCollector _metrics;

    public UserInfoQueueSender(IPublishEndpoint publishEndpoint, MetricsCollector metrics)
    {
        _publishEndpoint = publishEndpoint;
        _metrics = metrics;
    }


    public virtual async Task NameChangedEvent(long userId, string newFirstName, string newLastName)
    {
        var userChangeNameEvent = new UserChangedName()
        {
            UserId = userId,
            NewFirstName = newFirstName,
            NewLastName = newLastName
        };

        await _publishEndpoint.Publish(userChangeNameEvent);
        _metrics.Increment("user_events_published");
        _metrics.Increment("user_name_changed_published");
    }

    public virtual async Task UsernameChangedEvent(long userId, string newUsername)
    {
        var usernameChangedEvent = new UserChangedUsername()
        {
            NewUsername = newUsername,
            UserId = userId
        };

        await _publishEndpoint.Publish(usernameChangedEvent);
        _metrics.Increment("user_events_published");
        _metrics.Increment("user_username_changed_published");
    }

    public virtual async Task UserChangedAvatarEvent(long userId, string profilePictureUrl, string profilePicturePreviewUrl)
    {
        var userChangedAvatarEvent = new UserChangedAvatar()
        {
            UserId = userId,
            ProfilePictureUrl = profilePictureUrl,
            ProfilePictureUrlPreview = profilePicturePreviewUrl
        };

        await _publishEndpoint.Publish(userChangedAvatarEvent);
        _metrics.Increment("user_events_published");
        _metrics.Increment("user_avatar_changed_published");
    }

    public virtual async Task BioChangedEvent(long userId, string newBio)
    {
        var bioChangedEvent = new UserChangedBio()
        {
            UserId = userId,
            NewBio = newBio
        };

        await _publishEndpoint.Publish(bioChangedEvent);
        _metrics.Increment("user_events_published");
        _metrics.Increment("user_bio_changed_published");
    }

    public virtual async Task UserDeletedEvent(long userId)
    {
        var userDeletedEvent = new UserDeleted()
        {
            UserId = userId
        };

        await _publishEndpoint.Publish(userDeletedEvent);
        _metrics.Increment("user_events_published");
        _metrics.Increment("user_deleted_published");
    }
}
