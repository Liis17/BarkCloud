using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Proto.Identity;

using Google.Protobuf.WellKnownTypes;

using MediatR;

namespace BarkCloud.Identity.Features.ListWebAuthnCredentials;

public class ListWebAuthnCredentialsCommandHandler(
    UserContext userContext,
    IWebAuthnStorage webAuthnStorage)
    : IRequestHandler<ListWebAuthnCredentialsCommand, ListWebAuthnCredentialsResponse>
{
    public async Task<ListWebAuthnCredentialsResponse> Handle(ListWebAuthnCredentialsCommand request, CancellationToken cancellationToken)
    {
        var creds = await webAuthnStorage.GetCredentialsByUserId(userContext.UserId);

        var response = new ListWebAuthnCredentialsResponse();

        foreach (var c in creds.OrderByDescending(x => x.CreatedAt))
        {
            var item = new ListWebAuthnCredentialsResponse.Types.Credential
            {
                Id = c.Id.ToString(),
                Name = c.Name,
                CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(c.CreatedAt, DateTimeKind.Utc))
            };

            if (c.LastUsedAt.HasValue)
            {
                item.LastUsedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(c.LastUsedAt.Value, DateTimeKind.Utc));
            }

            response.Credentials.Add(item);
        }

        return response;
    }
}
