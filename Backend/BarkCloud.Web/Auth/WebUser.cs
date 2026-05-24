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
    UsernameTaken,
    EmailTaken,
    ValidationError,
    Error
}

public sealed record RegistrationResult(RegistrationOutcome Outcome, string? Message = null);
