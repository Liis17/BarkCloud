namespace BarkCloud.Files.Domain;

public static class FileActivityKind
{
    public const string Uploaded = "uploaded";
    public const string Attached = "attached";
    public const string Renamed = "renamed";
    public const string Moved = "moved";
    public const string Deleted = "deleted";
    public const string Restored = "restored";
    public const string Purged = "purged";
    public const string FavoriteAdded = "favorite_added";
    public const string FavoriteRemoved = "favorite_removed";
    public const string ShareCreated = "share_created";
    public const string ShareRevoked = "share_revoked";
    public const string SharedWithUser = "shared_with_user";
    public const string UserShareRevoked = "user_share_revoked";
    public const string AlbumAdded = "album_added";
    public const string AlbumRemoved = "album_removed";
}
