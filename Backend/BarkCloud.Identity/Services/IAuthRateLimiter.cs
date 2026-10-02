namespace BarkCloud.Identity.Services;

public interface IAuthRateLimiter
{
    /// <summary>Занимает попытку по доверенному адресу источника (<c>RequestContext.SourceIp</c>).</summary>
    /// <exception cref="BarkCloud.Shared.Exceptions.Identity.TooManyRequestsException">Лимит окна для источника исчерпан.</exception>
    Task EnsureSourceAsync(AuthLimits.Policy policy);

    /// <summary>Занимает попытку по субъекту (аккаунт, получатель письма). false — лимит окна исчерпан.</summary>
    Task<bool> TryReserveAsync(AuthLimits.Policy policy, string subject);

    /// <summary>Сбрасывает счётчик субъекта (после успешного входа).</summary>
    Task ResetAsync(AuthLimits.Policy policy, string subject);
}
