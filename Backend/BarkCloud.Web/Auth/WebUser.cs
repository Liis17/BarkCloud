namespace BarkCloud.Web.Auth;

/// <summary>Авторизованный веб-пользователь, выведенный из access-токена в cookie.</summary>
public sealed class WebUser
{
    public required long UserId { get; init; }

    public string? DeviceId { get; init; }

    public required string AccessToken { get; init; }
}

public enum LoginOutcome
{
    Success,
    NeedsOtp,
    WrongOtp,
    InvalidCredentials,
    Error
}

public sealed record LoginResult(LoginOutcome Outcome, string? Message = null);

public enum RegistrationOutcome
{
    Success,
    PendingConfirmation,
    UsernameTaken,
    EmailTaken,
    ValidationError,
    CodeInvalid,
    CodeExpired,
    Error
}

public sealed record RegistrationResult(RegistrationOutcome Outcome, string? Message = null, string? CodeId = null);

public enum PasswordResetOutcome
{
    Success,
    PendingConfirmation,
    ValidationError,
    CodeInvalid,
    CodeExpired,
    Error
}

public sealed record PasswordResetResult(PasswordResetOutcome Outcome, string? Message = null, string? ResetId = null);
