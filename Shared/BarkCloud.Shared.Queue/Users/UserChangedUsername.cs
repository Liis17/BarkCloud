namespace BarkCloud.Shared.Queue.Users;

public class UserChangedUsername
{
    public long UserId { get; set; }

    public string NewUsername { get; set; }
}