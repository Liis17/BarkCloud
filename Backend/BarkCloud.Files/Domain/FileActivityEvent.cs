namespace BarkCloud.Files.Domain;

public class FileActivityEvent
{
    public Guid Id { get; set; }

    public long OwnerId { get; set; }

    public Guid FileId { get; set; }

    public Guid? EntryId { get; set; }

    public long ActorUserId { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string DetailsJson { get; set; } = "{}";

    public DateTime CreatedAt { get; set; }
}
