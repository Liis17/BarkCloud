namespace BarkCloud.Users.Domain;

/// <summary>Видит ли пользователь <c>viewerId</c> профиль (имя, bio, аватар) пользователя <c>user</c>.</summary>
public static class ProfileVisibilityPolicy
{
    // Связи «контакт» в Users нет, поэтому Contacts для чужих трактуется как Nobody (fail-closed).
    // Отсутствие строки privacy = значение по умолчанию (Everyone).
    public static bool IsVisibleTo(User user, long viewerId)
        => user.Id == viewerId
           || (user.Privacy?.ProfileVisibility ?? PrivacyVisibility.Everyone) == PrivacyVisibility.Everyone;
}
