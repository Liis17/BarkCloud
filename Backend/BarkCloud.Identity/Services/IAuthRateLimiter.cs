namespace BarkCloud.Identity.Services;

public interface IAuthRateLimiter
{
    /// <summary>Занимает попытку по доверенному адресу источника (<c>RequestContext.SourceIp</c>).</summary>
    /// <exception cref="BarkCloud.Shared.Exceptions.Identity.TooManyRequestsException">Лимит окна для источника исчерпан.</exception>
    Task EnsureSourceAsync(AuthLimits.Policy policy);

    /// <summary>Занимает попытку по субъекту (аккаунт, получатель письма). false — лимит окна исчерпан.</summary>
    Task<bool> TryReserveAsync(AuthLimits.Policy policy, string subject);

    /// <summary>Занимает попытку ввода TOTP-кода под токеном (подтверждение/отключение/замена Authenticator).</summary>
    /// <exception cref="BarkCloud.Shared.Exceptions.Identity.TooManyRequestsException">Лимит окна для аккаунта исчерпан.</exception>
    Task EnsureTotpAttemptAsync(long userId);

    /// <summary>Сбрасывает счётчик субъекта (после успешного входа).</summary>
    Task ResetAsync(AuthLimits.Policy policy, string subject, CancellationToken cancellationToken = default);
}
