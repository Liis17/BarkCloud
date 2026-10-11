namespace BarkCloud.Drive.Contracts;

// Данные для WebAuthn-вызова в UI: options (challenge/allowCredentials), идентификатор
// серверного challenge и RP ID (домен). Пустой ChallengeId означает, что вход по ключу
// недоступен; Error содержит причину сетевой ошибки, если она известна.
public sealed class WebAuthnChallenge
{
    public string OptionsJson { get; set; } = string.Empty;

    public string ChallengeId { get; set; } = string.Empty;

    public string RpId { get; set; } = string.Empty;

    public string? Error { get; set; }
}
