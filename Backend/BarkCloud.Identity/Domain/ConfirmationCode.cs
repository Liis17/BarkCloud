using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Identity.Domain;

public class ConfirmationCode
{
    [Key]
    public Guid Id { get; set; }

    public string Value { get; set; }

    public DateTime Expires { get; set; }

    public long? OwnerId { get; set; }

    public ConfirmationCodeType Type { get; set; }

    /// <summary>Занятые попытки ввода кода — лимит на код, не зависящий от адреса источника.</summary>
    public int Attempts { get; set; }
}