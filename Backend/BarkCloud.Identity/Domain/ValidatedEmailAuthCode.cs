namespace BarkCloud.Identity.Domain;

public sealed record ValidatedEmailAuthCode(
    long UserId,
    EmailAuthCodePurpose Purpose,
    string Code,
    DateTime IssuedAt,
    DateTime ExpiresAt);
