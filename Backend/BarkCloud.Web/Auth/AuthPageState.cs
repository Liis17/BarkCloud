using System.Text.Json.Serialization;

namespace BarkCloud.Web.Auth;

public sealed record AuthPageState(
    string Screen,
    string ServerName,
    string Version,
    string CsrfToken,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FlowId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? FlowExpiresAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? CodeExpiresAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? RetryAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OtpMethod,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OtpMailStatus,
    AuthPageValues Values,
    IReadOnlyDictionary<string, string> FieldErrors,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FormError,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FormMessage,
    AuthPageFeatures Features,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ClearOtp,
    // Для экрана reset: сервер подтвердил email-код и ждёт код Authenticator.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool AuthenticatorRequired = false);

public sealed record AuthPageValues(
    [property: JsonPropertyName("login"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Login = null,
    [property: JsonPropertyName("username"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Username = null,
    [property: JsonPropertyName("email"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Email = null,
    [property: JsonPropertyName("first_name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FirstName = null,
    [property: JsonPropertyName("last_name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LastName = null,
    [property: JsonPropertyName("remember"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Remember = null,
    [property: JsonPropertyName("revoke_sessions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? RevokeSessions = null);

public sealed record AuthPageFeatures(bool RegistrationEnabled, bool EmailEnabled);
