namespace BarkCloud.Web.Auth;

/// <summary>Авторизованный веб-пользователь, выведенный из access-токена в cookie.</summary>
public sealed class WebUser
{
    public required long UserId { get; init; }

    public string? DeviceId { get; init; }

    public required string AccessToken { get; init; }

    /// <summary>Момент выдачи access-токена (<c>iat</c>); по нему сверяется момент отзыва сессии.</summary>
    public DateTime IssuedAt { get; init; }

    /// <summary>Идентификатор сессии (<c>x-session-id</c>); нет клейма — <c>null</c>.</summary>
    public long? SessionId { get; init; }
}

public enum LoginOutcome
{
    Success,
    NeedsOtp,
    WrongOtp,
    InvalidCredentials,
    TooManyAttempts,
    Error
}

public sealed record LoginResult(
    LoginOutcome Outcome,
    string? Message = null,
    string? OtpMethod = null,
    int? RetryAfterSeconds = null,
    string? OtpMailStatus = null);

public enum RegistrationOutcome
{
    Success,
    PendingConfirmation,
    UsernameTaken,
    EmailTaken,
    ValidationError,
    CodeInvalid,
    CodeExpired,
    SessionPending,
    Error
}

public sealed record RegistrationResult(
    RegistrationOutcome Outcome,
    string? Message = null,
    string? CodeId = null,
    string? PendingRefreshToken = null,
    string? Field = null,
    DateTimeOffset? PendingRefreshExpiresAt = null);

public enum PasswordResetOutcome
{
    Success,
    PendingConfirmation,
    AuthenticatorRequired,
    ValidationError,
    CodeInvalid,
    CodeExpired,
    TooManyAttempts,
    Error
}

public sealed record PasswordResetResult(
    PasswordResetOutcome Outcome,
    string? Message = null,
    string? ResetId = null,
    int? RetryAfterSeconds = null);
