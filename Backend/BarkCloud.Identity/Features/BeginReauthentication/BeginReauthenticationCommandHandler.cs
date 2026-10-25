using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Identity;
using BarkCloud.Shared.Queue.Notifications;

using Fido2NetLib;
using Fido2NetLib.Objects;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.Features.BeginReauthentication;

public sealed class BeginReauthenticationCommandHandler(
    UserContext userContext,
    IdentityContext context,
    IAuthPropertiesStorage authPropertiesStorage,
    IPasswordsStorage passwordsStorage,
    ReauthPasswordVerifier reauthPasswordVerifier,
    IWebAuthnStorage webAuthnStorage,
    ReauthenticationService reauthenticationService,
    IFido2 fido2,
    UsersServerApi.UsersServerApiClient usersClient,
    INotificationOutbox notificationOutbox,
    IAuthRateLimiter rateLimiter,
    MetricsCollector metrics,
    RequestContext requestContext,
    ILogger<BeginReauthenticationCommandHandler> logger)
    : IRequestHandler<BeginReauthenticationCommand, BeginReauthenticationResponse>
{
    private static readonly TimeSpan PermissionLifetime = TimeSpan.FromMinutes(5);

    public async Task<BeginReauthenticationResponse> Handle(BeginReauthenticationCommand request,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Начата повторная аутентификация пользователя {UserId} для действия {Action}",
            userContext.UserId, request.Action);
        if (userContext.SessionId is not { } sessionId
            || !await reauthenticationService.IsSessionActive(userContext.UserId, sessionId, cancellationToken))
            throw new ReauthenticationRequiredException();

        var credentialId = request.Action == ReauthenticationAction.RemovePasskey
            && long.TryParse(request.CredentialId, out var parsedId) ? parsedId : (long?)null;
        if (request.Action == ReauthenticationAction.RemovePasskey && credentialId is null)
            throw new ReauthenticationRequiredException();
        if (request.Action is not (ReauthenticationAction.AddPasskey or ReauthenticationAction.RemovePasskey
            or ReauthenticationAction.SetInitialPassword))
            throw new ReauthenticationRequiredException();

        if (request.Action == ReauthenticationAction.SetInitialPassword
            && !string.IsNullOrEmpty(request.Password))
            throw new ReauthenticationRequiredException();

        if (!string.IsNullOrEmpty(request.ChallengeId))
            return await ResendEmailCodeAsync(request, credentialId, sessionId, cancellationToken);

        if (string.IsNullOrEmpty(request.Password))
            return await BeginPasskeyChallengeAsync(request.Action, credentialId, sessionId, cancellationToken);

        var securityVersion = await passwordsStorage.GetSecurityStateVersion(userContext.UserId, cancellationToken);
        await reauthPasswordVerifier.VerifyAsync(userContext.UserId, request.Password, cancellationToken);
        var authProperties = await authPropertiesStorage.GetUserAuthProperties(userContext.UserId, cancellationToken);

        if (authProperties is { OtpEnabled: true })
        {
            if (string.IsNullOrEmpty(request.OtpCode))
                throw new OtpCodeNeedException("authenticator");
            await rateLimiter.EnsureTotpAttemptAsync(userContext.UserId);
            var totp = TotpVerificationService.Verify(userContext.UserId, authProperties.OtpSecret,
                authProperties.OtpSecretGeneration, request.OtpCode);
            if (totp is null)
                throw new NotValidOtpCodeException();

            return await GrantWithTotpAsync(request.Action, credentialId, sessionId, securityVersion, totp,
                cancellationToken);
        }

        if (authProperties is { EmailOtpEnabled: true })
            return await BeginEmailChallengeAsync(request.Action, credentialId, sessionId, securityVersion,
                null, cancellationToken);

        return await GrantWithPasswordAsync(request.Action, credentialId, sessionId, securityVersion,
            cancellationToken);
    }

    private async Task<BeginReauthenticationResponse> GrantWithTotpAsync(ReauthenticationAction action,
        long? credentialId, long sessionId, long securityVersion, ValidatedTotpCode totp,
        CancellationToken cancellationToken)
    {
        var (token, permission) = CreateGranted(action, credentialId, sessionId, securityVersion);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await IdentityOwnerLock.AcquireAsync(context, userContext.UserId, cancellationToken);
        if (await passwordsStorage.GetSecurityStateVersion(userContext.UserId, cancellationToken) != securityVersion
            || !await reauthenticationService.IsSessionActive(userContext.UserId, sessionId, cancellationToken)
            || !await authPropertiesStorage.TryConsumeTotpStep(totp.UserId, totp.Secret, totp.Generation,
                totp.TimeStep, cancellationToken))
            throw new ReauthenticationRequiredException();

        await CleanupExpiredAsync(cancellationToken);
        context.ReauthPermissions.Add(permission);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        metrics.Increment("reauth_password_success");
        return new BeginReauthenticationResponse { PermissionToken = token };
    }

    private async Task<BeginReauthenticationResponse> GrantWithPasswordAsync(ReauthenticationAction action,
        long? credentialId, long sessionId, long securityVersion, CancellationToken cancellationToken)
    {
        var (token, permission) = CreateGranted(action, credentialId, sessionId, securityVersion);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await IdentityOwnerLock.AcquireAsync(context, userContext.UserId, cancellationToken);
        if (await passwordsStorage.GetSecurityStateVersion(userContext.UserId, cancellationToken) != securityVersion
            || !await reauthenticationService.IsSessionActive(userContext.UserId, sessionId, cancellationToken))
            throw new ReauthenticationRequiredException();
        var currentAuthProperties = await authPropertiesStorage.GetUserAuthProperties(userContext.UserId, cancellationToken);
        if (currentAuthProperties is { OtpEnabled: true })
            throw new OtpCodeNeedException("authenticator");
        if (currentAuthProperties is { EmailOtpEnabled: true })
            throw new OtpCodeNeedException("email");

        await CleanupExpiredAsync(cancellationToken);
        context.ReauthPermissions.Add(permission);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        metrics.Increment("reauth_password_success");
        return new BeginReauthenticationResponse { PermissionToken = token };
    }

    private async Task<BeginReauthenticationResponse> BeginEmailChallengeAsync(ReauthenticationAction action,
        long? credentialId, long sessionId, long securityVersion, Guid? existingId,
        CancellationToken cancellationToken)
    {
        var contacts = await usersClient.GetUserContactsAsync(new GetUserContactsRequest { UserId = userContext.UserId },
            cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(contacts.Contact?.Email))
            throw new ReauthenticationRequiredException();

        var code = CodeGenerator.GenerateDigitalCode(6);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await IdentityOwnerLock.AcquireAsync(context, userContext.UserId, cancellationToken);
        if (await passwordsStorage.GetSecurityStateVersion(userContext.UserId, cancellationToken) != securityVersion
            || !await reauthenticationService.IsSessionActive(userContext.UserId, sessionId, cancellationToken))
            throw new ReauthenticationRequiredException();
        if (await authPropertiesStorage.GetUserAuthProperties(userContext.UserId, cancellationToken)
            is { OtpEnabled: true })
            throw new OtpCodeNeedException("authenticator");

        ReauthPermission permission;
        if (existingId is { } id)
        {
            permission = await context.ReauthPermissions.FirstOrDefaultAsync(x => x.Id == id
                && x.UserId == userContext.UserId && x.SessionId == sessionId
                && x.Action == (int)action && x.CredentialId == credentialId
                && x.Status == ReauthPermissionStatus.EmailPending && x.ExpiresAt > DateTime.UtcNow,
                cancellationToken) ?? throw new ReauthenticationRequiredException();
        }
        else
        {
            permission = new ReauthPermission
            {
                Id = Guid.NewGuid(), UserId = userContext.UserId, SessionId = sessionId,
                Action = (int)action, CredentialId = credentialId, SecurityStateVersion = securityVersion,
                Status = ReauthPermissionStatus.EmailPending, CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow + PermissionLifetime
            };
            context.ReauthPermissions.Add(permission);
        }

        var issue = await authPropertiesStorage.TryIssueEmailAuthCodeWithSnapshot(userContext.UserId,
            EmailAuthCodePurpose.Reauthentication, code, cancellationToken, permission.Id);
        if (issue.Sent)
        {
            var enqueued = await notificationOutbox.EnqueueAsync(userContext.UserId,
                NotificationType.ConfirmationAuth, "Код повторной аутентификации",
                new Dictionary<string, string>
                {
                    ["confirmation_code"] = code,
                    ["ip"] = requestContext.IpAddress ?? string.Empty,
                    ["devicename"] = requestContext.DeviceName ?? string.Empty,
                    ["os"] = requestContext.OperationSystem ?? string.Empty,
                    ["appname"] = $"{requestContext.AppName} v.{requestContext.AppVersion}",
                    ["datetime"] = DateTime.UtcNow.ToString("dd.MM.yyyy HH:mm:ss")
                }, cancellationToken);
            if (enqueued) metrics.Increment("notification_outbox_enqueued");
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new BeginReauthenticationResponse
        {
            ChallengeId = permission.Id.ToString(),
            RetryAfterSeconds = issue.RetryAfterSeconds
        };
    }

    private async Task<BeginReauthenticationResponse> ResendEmailCodeAsync(BeginReauthenticationCommand request,
        long? credentialId, long sessionId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.ChallengeId, out var challengeId))
            throw new ReauthenticationRequiredException();
        var permission = await context.ReauthPermissions.AsNoTracking().FirstOrDefaultAsync(x =>
            x.Id == challengeId && x.UserId == userContext.UserId && x.SessionId == sessionId
            && x.Action == (int)request.Action && x.CredentialId == credentialId
            && x.Status == ReauthPermissionStatus.EmailPending && x.ExpiresAt > DateTime.UtcNow,
            cancellationToken);
        if (permission is null || permission.SecurityStateVersion
            != await passwordsStorage.GetSecurityStateVersion(userContext.UserId, cancellationToken))
            throw new ReauthenticationRequiredException();

        return await BeginEmailChallengeAsync(request.Action, credentialId, sessionId,
            permission.SecurityStateVersion, permission.Id, cancellationToken);
    }

    private async Task<BeginReauthenticationResponse> BeginPasskeyChallengeAsync(ReauthenticationAction action,
        long? credentialId, long sessionId, CancellationToken cancellationToken)
    {
        var credentials = await webAuthnStorage.GetCredentialsByUserId(userContext.UserId);
        if (credentials.Count == 0 || credentialId is { } id && credentials.All(x => x.Id != id))
            throw new ReauthenticationRequiredException();

        var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = credentials.Select(x => new PublicKeyCredentialDescriptor(x.CredentialId)).ToList(),
            UserVerification = UserVerificationRequirement.Required
        });
        var challengeId = Guid.NewGuid();
        var version = await passwordsStorage.GetSecurityStateVersion(userContext.UserId, cancellationToken);
        var permission = new ReauthPermission
        {
            Id = Guid.NewGuid(), UserId = userContext.UserId, SessionId = sessionId,
            Action = (int)action, CredentialId = credentialId, SecurityStateVersion = version,
            Status = ReauthPermissionStatus.PasskeyPending, CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow + PermissionLifetime, WebAuthnChallengeId = challengeId
        };
        var webAuthnChallenge = new WebAuthnChallenge
        {
            Id = challengeId, UserId = userContext.UserId,
            Type = WebAuthnChallengeType.ReauthenticationAssertion, OptionsJson = options.ToJson(),
            ExpiresAt = permission.ExpiresAt, SessionId = sessionId, SecurityStateVersion = version,
            ReauthPermissionId = permission.Id
        };

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await IdentityOwnerLock.AcquireAsync(context, userContext.UserId, cancellationToken);
        if (await passwordsStorage.GetSecurityStateVersion(userContext.UserId, cancellationToken) != version
            || !await reauthenticationService.IsSessionActive(userContext.UserId, sessionId, cancellationToken))
            throw new ReauthenticationRequiredException();
        await CleanupExpiredAsync(cancellationToken);
        context.ReauthPermissions.Add(permission);
        await webAuthnStorage.SaveChallenge(webAuthnChallenge);
        await transaction.CommitAsync(cancellationToken);

        return new BeginReauthenticationResponse
        {
            ChallengeId = challengeId.ToString(), OptionsJson = options.ToJson()
        };
    }

    private (string Token, ReauthPermission Permission) CreateGranted(ReauthenticationAction action,
        long? credentialId, long sessionId, long securityVersion)
    {
        var token = ReauthenticationService.CreatePermissionToken(out var hash);
        return (token, new ReauthPermission
        {
            Id = Guid.NewGuid(), UserId = userContext.UserId, SessionId = sessionId,
            Action = (int)action, CredentialId = credentialId, SecurityStateVersion = securityVersion,
            PermissionTokenHash = hash, Status = ReauthPermissionStatus.Granted,
            CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow + PermissionLifetime
        });
    }

    private async Task CleanupExpiredAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await context.ReauthPermissions.Where(x => x.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);
        await context.WebAuthnChallenges.Where(x => x.ExpiresAt <= now
            || x.Type == WebAuthnChallengeType.ReauthenticationAssertion
                && !context.ReauthPermissions.Any(p => p.Id == x.ReauthPermissionId && p.ExpiresAt > now))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
