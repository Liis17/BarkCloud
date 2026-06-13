using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Proto.Identity;

using MediatR;

namespace BarkCloud.Identity.Features.RemoveWebAuthnCredential;

public class RemoveWebAuthnCredentialCommandHandler(
    UserContext userContext,
    IWebAuthnStorage webAuthnStorage,
    MetricsCollector metrics,
    ILogger<RemoveWebAuthnCredentialCommandHandler> logger)
    : IRequestHandler<RemoveWebAuthnCredentialCommand, RemoveWebAuthnCredentialResponse>
{
    public async Task<RemoveWebAuthnCredentialResponse> Handle(RemoveWebAuthnCredentialCommand request, CancellationToken cancellationToken)
    {
        if (long.TryParse(request.CredentialId, out var id))
        {
            var removed = await webAuthnStorage.RemoveCredential(userContext.UserId, id);
            if (removed)
            {
                metrics.Increment("webauthn_credentials_removed");
                logger.LogInformation("Удалён ключ безопасности {CredentialId} пользователя {UserId}", id, userContext.UserId);
            }
        }

        return new RemoveWebAuthnCredentialResponse();
    }
}
