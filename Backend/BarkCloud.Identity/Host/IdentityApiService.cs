using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Features.Auth;
using BarkCloud.Identity.Features.ConfirmAccount;
using BarkCloud.Identity.Features.ConfirmOtpVerification;
using BarkCloud.Identity.Features.CreateAccount;
using BarkCloud.Identity.Features.CreateToken;
using BarkCloud.Identity.Features.DisableOtpVerification;
using BarkCloud.Identity.Features.EnableOtpVerification;
using BarkCloud.Identity.Features.GetActiveSessions;
using BarkCloud.Identity.Features.ListOtpVerification;
using BarkCloud.Identity.Features.Logout;
using BarkCloud.Identity.Features.RemoveActiveSession;
using BarkCloud.Identity.Features.ResetPassword;
using BarkCloud.Identity.Features.BeginWebAuthnRegistration;
using BarkCloud.Identity.Features.CompleteWebAuthnRegistration;
using BarkCloud.Identity.Features.BeginWebAuthnAssertion;
using BarkCloud.Identity.Features.CompleteWebAuthnAssertion;
using BarkCloud.Identity.Features.ListWebAuthnCredentials;
using BarkCloud.Identity.Features.RemoveWebAuthnCredential;
using BarkCloud.Identity.Features.BeginReauthentication;
using BarkCloud.Identity.Features.CompleteReauthentication;
using BarkCloud.Identity.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Shared.Identity;

using Grpc.Core;

using MediatR;

using Microsoft.AspNetCore.Authorization;

namespace BarkCloud.Identity.Host;

using Features.ConfirmResetPassword;
using Features.SetPassword;

public class IdentityApiService : BarkCloud.Proto.Identity.IdentityApi.IdentityApiBase
{
    private readonly IMediator _mediator;
    private readonly JwtService _jwtService;
    private readonly MetricsCollector _metrics;

    public IdentityApiService(IMediator mediator, JwtService jwtService, MetricsCollector metrics)
    {
        _mediator = mediator;
        _jwtService = jwtService;
        _metrics = metrics;
    }

    public override async Task<AuthResponse> Auth(AuthRequest request, ServerCallContext context)
    {
        _metrics.Increment("auth_login_attempts");

        var command = new AuthCommand
        {
            Username = request.Username?.Trim(),
            Email = request.Email?.Trim(),
            OtpCode = request.OtpCode,
            Password = request.Password,
        };

        return await _mediator.Send(command, context.CancellationToken);
    }

    public override async Task<CreateTokenResponse> CreateToken(CreateTokenRequest request, ServerCallContext context)
    {
        _metrics.Increment("tokens_refresh_attempts");

        var command = new CreateTokenCommand
        {
            RefreshToken = request.RefreshToken,
        };

        return await _mediator.Send(command);
    }

    public override async Task<CreateAccountResponse> CreateAccount(CreateAccountRequest request, ServerCallContext context)
    {
        _metrics.Increment("account_creation_attempts");

        var command = new CreateAccountCommand
        {
            Username = request.Username?.Trim() ?? string.Empty,
            Email = request.Email?.Trim() ?? string.Empty,
            FirstName = request.FirstName?.Trim(),
            LastName = request.LastName?.Trim(),
        };

        return await _mediator.Send(command, context.CancellationToken);
    }

    public override Task<CreateAccountResponse> CreateAccountWithPassword(CreateAccountRequest request,
        ServerCallContext context)
    {
        _metrics.Increment("account_creation_attempts");

        return _mediator.Send(new CreateAccountCommand
        {
            Username = request.Username?.Trim() ?? string.Empty,
            Email = request.Email?.Trim() ?? string.Empty,
            FirstName = request.FirstName?.Trim() ?? string.Empty,
            LastName = request.LastName?.Trim() ?? string.Empty,
            InitialPassword = request.InitialPassword,
            RequiresInitialPassword = true
        }, context.CancellationToken);
    }

    public override Task<ConfirmAccountResponse> ConfirmAccount(ConfirmAccountRequest request, ServerCallContext context)
    {
        var command = new ConfirmAccountCommand
        {
            Code = request.CodeValue,
            CodeId = request.CodeId,
        };

        return _mediator.Send(command, context.CancellationToken);
    }

    public override Task<ConfirmAccountResponse> ConfirmAccountWithPassword(ConfirmAccountRequest request,
        ServerCallContext context)
    {
        return _mediator.Send(new ConfirmAccountCommand
        {
            Code = request.CodeValue,
            CodeId = request.CodeId,
            InitialPassword = request.InitialPassword,
            RequiresInitialPassword = true
        }, context.CancellationToken);
    }

    [Authorize(Policy = nameof(TokenType.User))]
    public override Task<GetActiveSessionsResponse> GetActiveSessions(GetActiveSessionsRequest request, ServerCallContext context)
    {
        var command = new GetActiveSessionsCommand();

        return _mediator.Send(command);
    }

    [Authorize(Policy = nameof(TokenType.User))]
    public override Task<RemoveActiveSessionResponse> RemoveActiveSession(RemoveActiveSessionRequest request, ServerCallContext context)
    {
        _metrics.Increment("session_removal_attempts");

        var command = new RemoveActiveSessionCommand()
        {
            DeviceId = request.DeviceId
        };

        return _mediator.Send(command);
    }

    [Authorize(Policy = nameof(TokenType.User))]
    public override Task<EnableOtpVerificationResponse> EnableOtpVerification(EnableOtpVerificationRequest request,
        ServerCallContext context)
    {
        var command = new EnableOtpVerificationCommand()
        {
            OptType = request.OtpType,
            Password = request.Password,
            CurrentOtpCode = request.CurrentOtpCode,
            CurrentEmailCode = request.CurrentEmailCode
        };

        return _mediator.Send(command);
    }

    [Authorize(Policy = nameof(TokenType.User))]
    public override Task<ConfirmOtpVerificationResponse> ConfirmOtpVerification(ConfirmOtpVerificationRequest request, ServerCallContext context)
    {
        var command = new ConfirmOtpVerificationCommand()
        {
            OtpCode = request.OtpCode
        };

        return _mediator.Send(command);
    }

    [Authorize(Policy = nameof(TokenType.User))]
    public override Task<DisableOtpVerificationResponse> DisableOtpVerification(DisableOtpVerificationRequest request, ServerCallContext context)
    {
        var command = new DisableOtpVerificationCommand
        {
            OtpCode = request.OtpCode,
            OptType = request.OtpType,
            Password = request.Password,
            CurrentEmailCode = request.CurrentEmailCode
        };

        return _mediator.Send(command);
    }

    [Authorize(Policy = nameof(TokenType.User))]
    public override Task<ListOtpVerificationResponse> ListOtpVerification(ListOtpVerificationRequest request, ServerCallContext context)
    {
        var command = new ListOtpVerificationCommand();

        return _mediator.Send(command);
    }

    public override async Task<ResetPasswordResponse> ResetPassword(ResetPasswordRequest request,
        ServerCallContext context)
    {
        _metrics.Increment("password_reset_requests");

        var command = new ResetPasswordCommand()
        {
            Email = request.Email?.Trim(),
            OtpType = (OtpType)(int)request.OtpType,
            Username = request.Username?.Trim()
        };

        return await _mediator.Send(command);
    }

    public override async Task<ConfirmResetPasswordResponse> ConfirmResetPassword(ConfirmResetPasswordRequest request, ServerCallContext context)
    {
        var command = new ConfirmResetPasswordCommand
        {
            OtpCode = request.OtpCode,
            ResetId = Guid.Parse(request.ResetId),
            NewPassword = request.NewPassword,
            RevokeOtherSessions = !request.HasRevokeOtherSessions || request.RevokeOtherSessions,
            ResetPasskeys = !request.HasResetPasskeys || request.ResetPasskeys,
            AuthenticatorCode = request.AuthenticatorCode
        };

        var result = await _mediator.Send(command, context.CancellationToken);

        return result;
    }

    [Authorize(Policy = nameof(TokenType.User))]
    public override async Task<SetPasswordResponse> SetPassword(SetPasswordRequest request, ServerCallContext context)
    {
        var command = new SetPasswordCommand
        {
            NewPassword = request.Password,
            OldPassword = request.OldPassword,
            PermissionToken = request.PermissionToken
        };

        await _mediator.Send(command);

        return new SetPasswordResponse();
    }

    [Authorize(Policy = nameof(TokenType.User))]
    public override Task<LogoutResponse> Logout(LogoutRequest request, ServerCallContext context)
    {
        return _mediator.Send(new LogoutCommand());
    }

    [Authorize(Policy = nameof(TokenType.User))]
    public override Task<BeginWebAuthnRegistrationResponse> BeginWebAuthnRegistration(BeginWebAuthnRegistrationRequest request, ServerCallContext context)
    {
        return _mediator.Send(new BeginWebAuthnRegistrationCommand { PermissionToken = request.PermissionToken },
            context.CancellationToken);
    }

    [Authorize(Policy = nameof(TokenType.User))]
    public override Task<CompleteWebAuthnRegistrationResponse> CompleteWebAuthnRegistration(CompleteWebAuthnRegistrationRequest request, ServerCallContext context)
    {
        return _mediator.Send(new CompleteWebAuthnRegistrationCommand
        {
            ChallengeId = request.ChallengeId,
            AttestationJson = request.AttestationJson,
            CredentialName = request.CredentialName,
            PermissionToken = request.PermissionToken
        }, context.CancellationToken);
    }

    // Публичный (вход без токена), как Auth.
    public override Task<BeginWebAuthnAssertionResponse> BeginWebAuthnAssertion(BeginWebAuthnAssertionRequest request, ServerCallContext context)
    {
        return _mediator.Send(new BeginWebAuthnAssertionCommand());
    }

    // Публичный (вход без токена), как Auth.
    public override Task<AuthResponse> CompleteWebAuthnAssertion(CompleteWebAuthnAssertionRequest request, ServerCallContext context)
    {
        _metrics.Increment("webauthn_login_attempts");

        return _mediator.Send(new CompleteWebAuthnAssertionCommand
        {
            ChallengeId = request.ChallengeId,
            AssertionJson = request.AssertionJson
        }, context.CancellationToken);
    }

    [Authorize(Policy = nameof(TokenType.User))]
    public override Task<ListWebAuthnCredentialsResponse> ListWebAuthnCredentials(ListWebAuthnCredentialsRequest request, ServerCallContext context)
    {
        return _mediator.Send(new ListWebAuthnCredentialsCommand());
    }

    [Authorize(Policy = nameof(TokenType.User))]
    public override Task<RemoveWebAuthnCredentialResponse> RemoveWebAuthnCredential(RemoveWebAuthnCredentialRequest request, ServerCallContext context)
    {
        return _mediator.Send(new RemoveWebAuthnCredentialCommand
        {
            CredentialId = request.CredentialId,
            PermissionToken = request.PermissionToken
        }, context.CancellationToken);
    }

    [Authorize(Policy = nameof(TokenType.User))]
    public override Task<BeginReauthenticationResponse> BeginReauthentication(BeginReauthenticationRequest request,
        ServerCallContext context)
    {
        return _mediator.Send(new BeginReauthenticationCommand
        {
            Action = request.Action,
            CredentialId = request.CredentialId,
            Password = request.Password,
            OtpCode = request.OtpCode,
            ChallengeId = request.ChallengeId
        }, context.CancellationToken);
    }

    [Authorize(Policy = nameof(TokenType.User))]
    public override Task<CompleteReauthenticationResponse> CompleteReauthentication(
        CompleteReauthenticationRequest request, ServerCallContext context)
    {
        return _mediator.Send(new CompleteReauthenticationCommand
        {
            ChallengeId = request.ChallengeId,
            AssertionJson = request.AssertionJson,
            EmailCode = request.EmailCode
        }, context.CancellationToken);
    }
}
