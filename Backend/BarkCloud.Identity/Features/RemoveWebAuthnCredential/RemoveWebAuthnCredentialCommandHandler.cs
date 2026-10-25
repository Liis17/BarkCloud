using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Shared.Exceptions.Identity;

using MediatR;

namespace BarkCloud.Identity.Features.RemoveWebAuthnCredential;

public class RemoveWebAuthnCredentialCommandHandler(
    UserContext userContext,
    IdentityContext context,
    IWebAuthnStorage webAuthnStorage,
    ReauthenticationService reauthenticationService,
    MetricsCollector metrics,
    ILogger<RemoveWebAuthnCredentialCommandHandler> logger)
    : IRequestHandler<RemoveWebAuthnCredentialCommand, RemoveWebAuthnCredentialResponse>
{
    public async Task<RemoveWebAuthnCredentialResponse> Handle(RemoveWebAuthnCredentialCommand request, CancellationToken cancellationToken)
    {
        if (!long.TryParse(request.CredentialId, out var id) || userContext.SessionId is not { } sessionId)
            throw new ReauthenticationRequiredException();

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await IdentityOwnerLock.AcquireAsync(context, userContext.UserId, cancellationToken);
        if (!await reauthenticationService.TryConsumeGrantedAsync(request.PermissionToken, userContext.UserId,
                sessionId, (int)ReauthenticationAction.RemovePasskey, id, cancellationToken))
            throw new ReauthenticationRequiredException();

        var removed = await webAuthnStorage.RemoveCredential(userContext.UserId, id);
        if (!removed)
            throw new ReauthenticationRequiredException();

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        metrics.Increment("webauthn_credentials_removed");
        logger.LogInformation("Удалён ключ безопасности {CredentialId} пользователя {UserId}", id, userContext.UserId);

        return new RemoveWebAuthnCredentialResponse();
    }
}
