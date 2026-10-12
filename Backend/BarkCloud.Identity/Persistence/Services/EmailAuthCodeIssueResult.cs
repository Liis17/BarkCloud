namespace BarkCloud.Identity.Persistence.Services;

public sealed record EmailAuthCodeIssueResult(bool Sent, DateTime IssuedAt, int RetryAfterSeconds);
