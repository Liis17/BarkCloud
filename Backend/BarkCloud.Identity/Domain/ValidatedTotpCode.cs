namespace BarkCloud.Identity.Domain;

public sealed record ValidatedTotpCode(long UserId, string Secret, long Generation, long TimeStep);
