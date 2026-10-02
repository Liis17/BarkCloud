using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Identity.Domain;

public class ResetPassword
{
    [Key] public Guid Id { get; set; }

    public long UserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public OtpType OtpType { get; set; }

    public string? OtpCode { get; set; }

    public bool IsApproved { get; set; }

    /// <summary>Занятые попытки ввода кода (верных и неверных) — лимит на запрос сброса, не зависящий от адреса источника.</summary>
    public int OtpAttempts { get; set; }
}
