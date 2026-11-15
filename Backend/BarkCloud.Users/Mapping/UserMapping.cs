using BarkCloud.Proto.Users;

using Google.Protobuf.WellKnownTypes;

namespace BarkCloud.Users.Mapping;

public static class UserMapping
{
    public static User ToGrpc(this Domain.User domainUser)
    {
        return new User
        {
            FirstName = domainUser.FirstName,
            Id = domainUser.Id,
            LastName = domainUser.LastName,
            RegistrationDate = Timestamp.FromDateTime(domainUser.RegistrationDate),
            Username = domainUser.Username,
            ProfilePicture = domainUser.ProfilePicture ?? string.Empty,
            ProfilePicturePreview = domainUser.ProfilePicturePreviewUrl ?? string.Empty,
            StorageLimitGb = domainUser.StorageLimitGb,
            Bio = domainUser.Bio ?? string.Empty,
        };
    }

    /// <summary>Профиль глазами <paramref name="viewerId"/>: при скрытом профиле без имени, bio и аватара.</summary>
    public static User ToGrpcForViewer(this Domain.User domainUser, long viewerId)
    {
        var user = domainUser.ToGrpc();

        if (Domain.ProfileVisibilityPolicy.IsVisibleTo(domainUser, viewerId))
        {
            return user;
        }

        user.FirstName = string.Empty;
        user.LastName = string.Empty;
        user.Bio = string.Empty;
        user.ProfilePicture = string.Empty;
        user.ProfilePicturePreview = string.Empty;

        return user;
    }
}
